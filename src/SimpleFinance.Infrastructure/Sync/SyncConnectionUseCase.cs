using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Application.Common;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Sync;

/// <summary>
/// THE single shared reconciler implementation (ISyncReconciler, 02-01's seam) — both the
/// webhook processor (02-05) and DailySyncJob/manual-sync (02-06) call this exact type. Per
/// ARCHITECTURE.md Anti-Pattern 1, there must never be a second implementation.
///
/// Deliberately placed in Infrastructure (not Application, despite the "Application.Sync"
/// framing in 02-04-PLAN.md's prose) because it depends directly on SimpleFinanceDbContext —
/// putting it in Application would require an Application -> Infrastructure ProjectReference,
/// which is circular (Infrastructure already references Application to implement
/// IFinancialDataProvider/ICategorizationService/IDistributedLockProvider). This mirrors the
/// same plan's own sibling classes (PostgresAdvisoryLockProvider, MappingOnlyCategorizationService,
/// SyncServiceCollectionExtensions), which are all Infrastructure-namespaced for the identical
/// reason. Application-layer code only ever depends on ISyncReconciler (ARCHITECTURE.md Pattern 1).
///
/// Task 1 scope: Postgres advisory lock wrapping the whole run (SYNC-07), Connection status
/// refresh, and idempotent Account/CreditCardDetail/Investment upsert-by-Pluggy-id. Transaction
/// reconciliation (Task 2) and SyncLog auditing (Task 3) extend this same class.
/// </summary>
public sealed class SyncConnectionUseCase : ISyncReconciler
{
    private const int FallbackMatchDateToleranceDays = 3;

    private readonly IFinancialDataProvider _provider;
    private readonly SimpleFinanceDbContext _db;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly ICategorizationService _categorizationService;
    private readonly ITransferDetectionService _transferDetectionService;
    private readonly IBackgroundJobClient _jobClient;
    private readonly ILogger<SyncConnectionUseCase> _logger;

    public SyncConnectionUseCase(
        IFinancialDataProvider provider,
        SimpleFinanceDbContext db,
        IDistributedLockProvider lockProvider,
        ICategorizationService categorizationService,
        ITransferDetectionService transferDetectionService,
        IBackgroundJobClient jobClient,
        ILogger<SyncConnectionUseCase>? logger = null)
    {
        _provider = provider;
        _db = db;
        _lockProvider = lockProvider;
        _categorizationService = categorizationService;
        _transferDetectionService = transferDetectionService;
        _jobClient = jobClient;
        _logger = logger ?? NullLogger<SyncConnectionUseCase>.Instance;
    }

    public async Task<SyncResult> SyncConnectionAsync(Guid connectionId, SyncTrigger trigger, CancellationToken ct)
    {
        // Wraps the ENTIRE method body so a second caller can never observe a
        // partially-reconciled state (SYNC-07).
        await using var lockHandle = await _lockProvider.AcquireAsync($"sync:{connectionId}", ct);

        var startedAt = DateTime.UtcNow;
        var counts = new SyncCounts();

        try
        {
            var connection = await _db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
                ?? throw new ConnectionNotFoundException(connectionId);

            var itemStatus = await _provider.GetItemStatusAsync(connection.PluggyItemId, ct);
            ApplyItemStatus(connection, itemStatus);

            var providerAccounts = await _provider.GetAccountsAsync(connection.PluggyItemId, ct);
            var creditAccounts = new List<(Guid AccountId, ProviderAccount ProviderAccount)>();
            foreach (var providerAccount in providerAccounts)
            {
                var accountId = await UpsertAccountAsync(connection, providerAccount, counts, ct);
                await ReconcileTransactionsAsync(connection, accountId, providerAccount.Id, counts, ct);
                if (ParseAccountType(providerAccount.Type) == AccountType.Credit)
                {
                    creditAccounts.Add((accountId, providerAccount));
                }
            }

            var providerInvestments = await _provider.GetInvestmentsAsync(connection.PluggyItemId, ct);
            foreach (var providerInvestment in providerInvestments)
            {
                await UpsertInvestmentAsync(connection, providerInvestment, counts, ct);
            }

            await _db.SaveChangesAsync(ct);

            // Credit-card bill reconciliation runs after transactions are persisted so future-installment
            // cards (e.g. Mercado Pago, where Account.Balance sums all future PENDING installments) can
            // compute the active billing cycle's invoice cleanly when /bills has not yet published it.
            var billDetailsChanged = false;
            foreach (var (accountId, providerAccount) in creditAccounts)
            {
                billDetailsChanged |= await ReconcileCreditCardBillSummaryAsync(accountId, providerAccount, ct);
            }

            if (billDetailsChanged)
            {
                await _db.SaveChangesAsync(ct);
            }

            // TXN-05 enrichment step: internal-transfer detection runs after every successful
            // sync's main upsert save, before the SyncLog write -- so a failure here is caught by
            // this same try/catch and recorded as a Failed SyncLog, consistent with the rest of
            // this method's error handling (03-02-PLAN.md's documented hook placement).
            await _transferDetectionService.DetectTransfersForUserAsync(connection.UserId, ct);

            var finishedAt = DateTime.UtcNow;
            _db.SyncLogs.Add(BuildSyncLog(connectionId, trigger, startedAt, finishedAt, SyncStatus.Success, counts, error: null));
            await _db.SaveChangesAsync(ct);

            // AI-CAT-01/05: after a successful reconcile, kick the AI cascade for the newly-arrived
            // transactions OUT OF BAND (16-04). Chain embedding -> categorization so the kNN level has
            // vectors to compare against; if this Hangfire version lacks ContinueJobWith the fallback
            // enqueues both independently (categorization tolerates missing embeddings, falling through
            // to the LLM/deterministic path). The inline rules+mapping categorization above is
            // unchanged -- the AI cascade owns only the kNN/LLM steps, deferred to a job (16-RESEARCH
            // §4). Guarded so an enqueue failure NEVER turns a successful sync into a failed one.
            EnqueueAiCascade();

            return new SyncResult(
                SyncStatus.Success,
                counts.TransactionsAdded,
                counts.TransactionsUpdated,
                counts.AccountsAdded,
                counts.AccountsUpdated,
                counts.InvestmentsAdded,
                counts.InvestmentsUpdated,
                null);
        }
        catch (Exception ex)
        {
            var finishedAt = DateTime.UtcNow;
            try
            {
                // Best-effort only: discard any partial Connection/Account/Transaction/
                // Investment writes still tracked from the failed run, so a failure persists
                // NOTHING except this one audit row (SYNC-05/T-02-10's mitigation) -- never mask
                // the original exception if even this SyncLog save fails.
                _db.ChangeTracker.Clear();
                _db.SyncLogs.Add(BuildSyncLog(connectionId, trigger, startedAt, finishedAt, SyncStatus.Failed, counts, ex.Message));
                await _db.SaveChangesAsync(ct);
            }
            catch
            {
                // Swallowed deliberately -- the ORIGINAL exception below is what must surface to
                // the caller (02-05/02-06's own retry/logging), never a masking SyncLog failure.
            }

            throw;
        }
    }

    /// <summary>
    /// Enqueues AiEmbeddingJob then AiCategorizationJob (chained so categorization runs after the
    /// embeddings exist), then chains AiAnomalyJob after categorization (18-05, AI-INS-02/03) so newly
    /// categorized transactions are scanned for anomalies as they arrive. All three jobs no-op when
    /// AI/their feature flag is disabled and degrade gracefully on budget. Any failure here (e.g. a
    /// transient Hangfire storage hiccup) is swallowed -- enqueueing the AI follow-up must never fail an
    /// already-successful reconcile; the nightly ai-anomaly recurring job is the backstop for a missed
    /// enqueue. Under the Testing environment IBackgroundJobClient is a NoOp, so this is a cheap no-op there.
    /// </summary>
    private void EnqueueAiCascade()
    {
        try
        {
            var embeddingJobId = _jobClient.Enqueue<AiEmbeddingJob>(j => j.ExecuteAsync(CancellationToken.None));
            var categorizationJobId = _jobClient.ContinueJobWith<AiCategorizationJob>(embeddingJobId, j => j.ExecuteAsync(CancellationToken.None));
            _jobClient.ContinueJobWith<AiAnomalyJob>(categorizationJobId, j => j.ExecuteAsync(CancellationToken.None));
        }
        catch
        {
            // Deliberately swallowed -- the sync already succeeded and its SyncLog is committed; a
            // failed AI enqueue is picked up by the nightly recurring ai-embedding/ai-categorization
            // backstops, never surfaced to the sync caller.
        }
    }

    private static SyncLog BuildSyncLog(
        Guid connectionId, SyncTrigger trigger, DateTime startedAt, DateTime finishedAt,
        SyncStatus status, SyncCounts counts, string? error)
    {
        var now = DateTime.UtcNow;
        return new SyncLog
        {
            Id = Guid.NewGuid(),
            ConnectionId = connectionId,
            TriggeredBy = trigger,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            Status = status,
            TransactionsAdded = counts.TransactionsAdded,
            TransactionsUpdated = counts.TransactionsUpdated,
            AccountsAdded = counts.AccountsAdded,
            AccountsUpdated = counts.AccountsUpdated,
            InvestmentsAdded = counts.InvestmentsAdded,
            InvestmentsUpdated = counts.InvestmentsUpdated,
            Error = error,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static void ApplyItemStatus(Connection connection, ProviderItemStatus itemStatus)
    {
        connection.Status = ParseConnectionStatus(itemStatus.Status);
        connection.ExecutionStatus = itemStatus.ExecutionStatus;
        connection.StatusDetail = itemStatus.StatusDetail;
        connection.ErrorCode = itemStatus.ErrorCode;
        connection.ErrorMessage = itemStatus.ErrorMessage;
        connection.LastSyncAt = DateTime.UtcNow;
        connection.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<Guid> UpsertAccountAsync(Connection connection, ProviderAccount pa, SyncCounts counts, CancellationToken ct)
    {
        var existing = await _db.Accounts
            .FirstOrDefaultAsync(a => a.ConnectionId == connection.Id && a.PluggyAccountId == pa.Id, ct);

        var type = ParseAccountType(pa.Type);
        var subtype = ParseAccountSubtype(pa.Subtype);

        if (existing is null)
        {
            var now = DateTime.UtcNow;
            var account = new Account
            {
                Id = Guid.NewGuid(),
                UserId = connection.UserId,
                ConnectionId = connection.Id,
                PluggyAccountId = pa.Id,
                Type = type,
                Subtype = subtype,
                Name = pa.Name,
                MarketingName = pa.MarketingName,
                NumberMasked = pa.NumberMasked,
                Owner = pa.Owner,
                Balance = pa.Balance,
                AutomaticallyInvestedBalance = pa.AutomaticallyInvestedBalance,
                CurrencyCode = pa.CurrencyCode,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Accounts.Add(account);
            counts.AccountsAdded++;

            await UpsertCreditCardDetailAsync(account.Id, pa.CreditData, ct);
            return account.Id;
        }

        var changed = false;
        changed |= SetIfChanged(() => existing.Type, v => existing.Type = v, type);
        changed |= SetIfChanged(() => existing.Subtype, v => existing.Subtype = v, subtype);
        changed |= SetIfChanged(() => existing.Name, v => existing.Name = v, pa.Name);
        changed |= SetIfChanged(() => existing.MarketingName, v => existing.MarketingName = v, pa.MarketingName);
        changed |= SetIfChanged(() => existing.NumberMasked, v => existing.NumberMasked = v, pa.NumberMasked);
        changed |= SetIfChanged(() => existing.Owner, v => existing.Owner = v, pa.Owner);
        changed |= SetIfChanged(() => existing.Balance, v => existing.Balance = v, pa.Balance);
        changed |= SetIfChanged(() => existing.AutomaticallyInvestedBalance, v => existing.AutomaticallyInvestedBalance = v, pa.AutomaticallyInvestedBalance);
        changed |= SetIfChanged(() => existing.CurrencyCode, v => existing.CurrencyCode = v, pa.CurrencyCode);

        if (changed)
        {
            existing.UpdatedAt = DateTime.UtcNow;
            counts.AccountsUpdated++;
        }

        await UpsertCreditCardDetailAsync(existing.Id, pa.CreditData, ct);
        return existing.Id;
    }

    private async Task UpsertCreditCardDetailAsync(Guid accountId, ProviderCreditData? cd, CancellationToken ct)
    {
        if (cd is null)
        {
            return;
        }

        var existing = await _db.CreditCardDetails.FirstOrDefaultAsync(c => c.AccountId == accountId, ct);
        var status = ParseCreditCardStatus(cd.Status);

        if (existing is null)
        {
            var now = DateTime.UtcNow;
            _db.CreditCardDetails.Add(new CreditCardDetail
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Brand = cd.Brand,
                Level = cd.Level,
                BrandAdditionalInfo = cd.BrandAdditionalInfo,
                CreditLimit = cd.CreditLimit,
                AvailableCredit = cd.AvailableCreditLimit,
                BalanceCloseDate = cd.BalanceCloseDate,
                BalanceDueDate = cd.BalanceDueDate,
                MinimumPayment = cd.MinimumPayment,
                Status = status,
                HolderType = cd.HolderType,
                CreatedAt = now,
                UpdatedAt = now,
            });
            return;
        }

        var changed = false;
        changed |= SetIfChanged(() => existing.Brand, v => existing.Brand = v, cd.Brand);
        changed |= SetIfChanged(() => existing.Level, v => existing.Level = v, cd.Level);
        changed |= SetIfChanged(() => existing.BrandAdditionalInfo, v => existing.BrandAdditionalInfo = v, cd.BrandAdditionalInfo);
        changed |= SetIfChanged(() => existing.CreditLimit, v => existing.CreditLimit = v, cd.CreditLimit);
        changed |= SetIfChanged(() => existing.AvailableCredit, v => existing.AvailableCredit = v, cd.AvailableCreditLimit);
        if (cd.BalanceCloseDate is not null)
        {
            changed |= SetIfChanged(() => existing.BalanceCloseDate, v => existing.BalanceCloseDate = v, NormalizeUtc(cd.BalanceCloseDate));
        }
        if (cd.BalanceDueDate is not null)
        {
            changed |= SetIfChanged(() => existing.BalanceDueDate, v => existing.BalanceDueDate = v, NormalizeUtc(cd.BalanceDueDate));
        }
        if (cd.MinimumPayment is not null)
        {
            changed |= SetIfChanged(() => existing.MinimumPayment, v => existing.MinimumPayment = v, cd.MinimumPayment);
        }
        changed |= SetIfChanged(() => existing.Status, v => existing.Status = v, status);
        changed |= SetIfChanged(() => existing.HolderType, v => existing.HolderType = v, cd.HolderType);

        if (changed)
        {
            existing.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task<bool> ReconcileCreditCardBillSummaryAsync(
        Guid accountId, ProviderAccount pa, CancellationToken ct)
    {
        var card = await _db.CreditCardDetails.FirstOrDefaultAsync(c => c.AccountId == accountId, ct);
        if (card is null)
        {
            return false;
        }

        IReadOnlyList<ProviderCreditCardBill> bills;
        try
        {
            bills = await _provider.GetCreditCardBillsAsync(pa.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch credit card bills for account {AccountId}; keeping existing bill summary.", accountId);
            bills = [];
        }

        var orderedBills = bills
            .OrderByDescending(b => b.DueDate ?? b.BillClosingDate ?? DateTime.MinValue)
            .ToList();

        var latestUnpaidBill = orderedBills.FirstOrDefault(b => b.TotalAmount - b.PaidAmount > 0.01m);

        var targetCloseDate = card.BalanceCloseDate;
        var targetDueDate = card.BalanceDueDate;
        var targetMinPayment = card.MinimumPayment;
        decimal? targetStatementBalance = card.LastStatementBalance;

        if (latestUnpaidBill is not null)
        {
            targetCloseDate = latestUnpaidBill.BillClosingDate ?? targetCloseDate;
            targetDueDate = latestUnpaidBill.DueDate ?? targetDueDate;
            targetMinPayment = latestUnpaidBill.MinimumPaymentAmount ?? targetMinPayment;
            targetStatementBalance = Math.Max(0m, latestUnpaidBill.TotalAmount - latestUnpaidBill.PaidAmount);
        }
        else if (orderedBills.Count > 0)
        {
            var latestPaid = orderedBills[0];
            var todayBrt = DateTime.UtcNow.AddHours(-3).Date;

            var projectedClose = latestPaid.BillClosingDate;
            var projectedDue = latestPaid.DueDate ?? card.BalanceDueDate;

            if (projectedDue is not null)
            {
                var guard = 0;
                while (projectedDue.Value.Date < todayBrt && guard++ < 24)
                {
                    projectedDue = projectedDue.Value.AddMonths(1);
                    if (projectedClose is not null)
                    {
                        projectedClose = projectedClose.Value.AddMonths(1);
                    }
                }
            }

            targetCloseDate = projectedClose ?? targetCloseDate;
            targetDueDate = projectedDue ?? targetDueDate;

            // When a card reports future-month PENDING installments in Account.Balance (e.g. Mercado Pago),
            // isolate ONLY the active billing cycle up to the closing cutoff so "Current invoice" does not
            // include future months' installments.
            if (targetCloseDate is not null)
            {
                var pending = await _db.Transactions
                    .Where(t => t.AccountId == accountId
                        && t.Status == TransactionStatus.Pending
                        && t.BillId == null)
                    .Select(t => new { t.Date, t.Amount, t.Type, t.Description })
                    .ToListAsync(ct);

                var closeCutoffUtc = DateTime.SpecifyKind(targetCloseDate.Value.Date, DateTimeKind.Utc).AddHours(6);
                var hasFutureCycles = pending.Any(t => t.Date > closeCutoffUtc);

                if (hasFutureCycles)
                {
                    var cycleTotal = pending
                        .Where(t => t.Date <= closeCutoffUtc && !IsCardPaymentCredit(t.Type, t.Description))
                        .Sum(t => t.Type == TransactionType.Debit ? Math.Abs(t.Amount) : -Math.Abs(t.Amount));

                    targetStatementBalance = cycleTotal > 0m ? cycleTotal : null;
                }
                else
                {
                    targetStatementBalance = null;
                }
            }
        }

        var changed = false;
        changed |= SetIfChanged(() => card.BalanceCloseDate, v => card.BalanceCloseDate = v, NormalizeUtc(targetCloseDate));
        changed |= SetIfChanged(() => card.BalanceDueDate, v => card.BalanceDueDate = v, NormalizeUtc(targetDueDate));
        changed |= SetIfChanged(() => card.MinimumPayment, v => card.MinimumPayment = v, targetMinPayment);
        changed |= SetIfChanged(() => card.LastStatementBalance, v => card.LastStatementBalance = v, targetStatementBalance);

        if (changed)
        {
            card.UpdatedAt = DateTime.UtcNow;
        }

        return changed;
    }

    private static DateTime? NormalizeUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } => value,
        { Kind: DateTimeKind.Unspecified } => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        _ => value.Value.ToUniversalTime(),
    };

    private static bool IsCardPaymentCredit(TransactionType type, string? description) =>
        type == TransactionType.Credit
        && !string.IsNullOrWhiteSpace(description)
        && description.Contains("pagamento", StringComparison.OrdinalIgnoreCase);

    private async Task UpsertInvestmentAsync(Connection connection, ProviderInvestment pi, SyncCounts counts, CancellationToken ct)
    {
        var existing = await _db.Investments
            .FirstOrDefaultAsync(i => i.ConnectionId == connection.Id && i.PluggyInvestmentId == pi.Id, ct);

        var type = ParseInvestmentType(pi.Type);

        if (existing is null)
        {
            var now = DateTime.UtcNow;
            _db.Investments.Add(new Investment
            {
                Id = Guid.NewGuid(),
                UserId = connection.UserId,
                ConnectionId = connection.Id,
                PluggyInvestmentId = pi.Id,
                Name = pi.Name,
                Type = type,
                Subtype = pi.Subtype,
                Institution = pi.Institution,
                IssuerName = pi.IssuerName,
                Balance = pi.Balance,
                Value = pi.Value,
                Quantity = pi.Quantity,
                AmountInvested = pi.AmountInvested,
                LastMonthRate = pi.LastMonthRate,
                AnnualRate = pi.AnnualRate,
                Date = pi.Date,
                DueDate = pi.DueDate,
                CurrencyCode = pi.CurrencyCode,
                CreatedAt = now,
                UpdatedAt = now,
            });
            counts.InvestmentsAdded++;
            return;
        }

        var changed = false;
        changed |= SetIfChanged(() => existing.Name, v => existing.Name = v, pi.Name);
        changed |= SetIfChanged(() => existing.Type, v => existing.Type = v, type);
        changed |= SetIfChanged(() => existing.Subtype, v => existing.Subtype = v, pi.Subtype);
        changed |= SetIfChanged(() => existing.Institution, v => existing.Institution = v, pi.Institution);
        changed |= SetIfChanged(() => existing.IssuerName, v => existing.IssuerName = v, pi.IssuerName);
        changed |= SetIfChanged(() => existing.Balance, v => existing.Balance = v, pi.Balance);
        changed |= SetIfChanged(() => existing.Value, v => existing.Value = v, pi.Value);
        changed |= SetIfChanged(() => existing.Quantity, v => existing.Quantity = v, pi.Quantity);
        changed |= SetIfChanged(() => existing.AmountInvested, v => existing.AmountInvested = v, pi.AmountInvested);
        changed |= SetIfChanged(() => existing.LastMonthRate, v => existing.LastMonthRate = v, pi.LastMonthRate);
        changed |= SetIfChanged(() => existing.AnnualRate, v => existing.AnnualRate = v, pi.AnnualRate);
        changed |= SetIfChanged(() => existing.Date, v => existing.Date = v, pi.Date);
        changed |= SetIfChanged(() => existing.DueDate, v => existing.DueDate = v, pi.DueDate);
        changed |= SetIfChanged(() => existing.CurrencyCode, v => existing.CurrencyCode = v, pi.CurrencyCode);

        if (changed)
        {
            existing.UpdatedAt = DateTime.UtcNow;
            counts.InvestmentsUpdated++;
        }
    }

    private async Task ReconcileTransactionsAsync(
        Connection connection, Guid accountId, string pluggyAccountId, SyncCounts counts, CancellationToken ct)
    {
        var providerTransactions = await _provider.GetTransactionsAsync(pluggyAccountId, ct);

        foreach (var providerTransaction in providerTransactions)
        {
            await ReconcileOneTransactionAsync(connection, accountId, providerTransaction, counts, ct);
        }
    }

    private async Task ReconcileOneTransactionAsync(
        Connection connection, Guid accountId, ProviderTransaction pt, SyncCounts counts, CancellationToken ct)
    {
        var existing = await _db.Transactions
            .FirstOrDefaultAsync(t => t.AccountId == accountId && t.PluggyTransactionId == pt.Id, ct);

        if (existing is not null)
        {
            ApplyTransactionFields(existing, pt, counts, forceCountAsUpdate: false);
            return;
        }

        // FALLBACK MATCH: RESEARCH.md's confirmed finding -- "transaction ID not guaranteed
        // stable across PENDING->POSTED" (Pluggy retrieves PENDING vs POSTED card-installment
        // transactions from two different collection paths, without an explicit stability
        // guarantee on `id`) -- matches PITFALLS.md Pitfall 2 exactly. A +/-3-day window is used
        // because PATCH /items/{id} re-pulls up to ~4 days of prior data (RESEARCH.md), so a
        // 3-day tolerance comfortably covers legitimate PENDING->POSTED re-representation without
        // over-matching. If zero or MORE THAN ONE candidate matches (ambiguous), never guess --
        // fall through to inserting a new row (T-02-11's mitigation).
        // TODO: validate this heuristic against real sandbox installment scenarios (Basic/MFA/
        // error-path connectors) before production go-live -- intentionally left open in Phase 2
        // since live sandbox credentials aren't available in this environment (CONTEXT.md's
        // Specifics section).
        var dateFrom = pt.Date.AddDays(-FallbackMatchDateToleranceDays);
        var dateTo = pt.Date.AddDays(FallbackMatchDateToleranceDays);

        var candidates = await _db.Transactions
            .Where(t => t.AccountId == accountId
                && t.Status == TransactionStatus.Pending
                && t.Amount == pt.Amount
                && t.Date >= dateFrom && t.Date <= dateTo)
            .ToListAsync(ct);

        var descriptionMatches = candidates
            .Where(t => string.Equals(t.Description, pt.Description, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (descriptionMatches.Count == 1)
        {
            var matched = descriptionMatches[0];
            matched.PluggyTransactionId = pt.Id;
            ApplyTransactionFields(matched, pt, counts, forceCountAsUpdate: true);
            return;
        }

        var categorization = await _categorizationService.CategorizeAsync(
            connection.UserId,
            new CategorizationInput(pt.Description, pt.MerchantName, pt.Amount, pt.PluggyCategoryId, pt.PluggyCategory),
            ct);
        var now = DateTime.UtcNow;

        _db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = connection.UserId,
            AccountId = accountId,
            PluggyTransactionId = pt.Id,
            Date = pt.Date,
            Description = pt.Description,
            DescriptionRaw = pt.DescriptionRaw,
            Amount = pt.Amount,
            Type = ParseTransactionType(pt.Type),
            CurrencyCode = pt.CurrencyCode,
            AmountInAccountCurrency = pt.AmountInAccountCurrency,
            BalanceAfter = pt.BalanceAfter,
            Status = ParseTransactionStatus(pt.Status),
            MerchantName = pt.MerchantName,
            MerchantDocument = pt.MerchantDocument,
            PluggyCategory = pt.PluggyCategory,
            PluggyCategoryId = pt.PluggyCategoryId,
            CategoryId = categorization.CategoryId,
            CategorySource = categorization.Source,
            IsTransfer = categorization.MarkAsTransfer,
            InstallmentNumber = pt.InstallmentNumber,
            TotalInstallments = pt.TotalInstallments,
            TotalInstallmentsAmount = pt.TotalInstallmentsAmount,
            BillId = pt.BillId,
            PaymentDataJson = pt.PaymentDataJson,
            CreatedAt = now,
            UpdatedAt = now,
        });
        counts.TransactionsAdded++;
    }

    private static void ApplyTransactionFields(Transaction existing, ProviderTransaction pt, SyncCounts counts, bool forceCountAsUpdate)
    {
        var changed = forceCountAsUpdate;
        var status = ParseTransactionStatus(pt.Status);

        changed |= SetIfChanged(() => existing.Status, v => existing.Status = v, status);
        changed |= SetIfChanged(() => existing.Amount, v => existing.Amount = v, pt.Amount);
        changed |= SetIfChanged(() => existing.BalanceAfter, v => existing.BalanceAfter = v, pt.BalanceAfter);
        changed |= SetIfChanged(() => existing.BillId, v => existing.BillId = v, pt.BillId);
        changed |= SetIfChanged(() => existing.InstallmentNumber, v => existing.InstallmentNumber = v, pt.InstallmentNumber);
        changed |= SetIfChanged(() => existing.TotalInstallments, v => existing.TotalInstallments = v, pt.TotalInstallments);
        changed |= SetIfChanged(() => existing.TotalInstallmentsAmount, v => existing.TotalInstallmentsAmount = v, pt.TotalInstallmentsAmount);
        changed |= SetIfChanged(() => existing.PluggyCategory, v => existing.PluggyCategory = v, pt.PluggyCategory);
        changed |= SetIfChanged(() => existing.PluggyCategoryId, v => existing.PluggyCategoryId = v, pt.PluggyCategoryId);

        if (changed)
        {
            existing.UpdatedAt = DateTime.UtcNow;
            counts.TransactionsUpdated++;
        }
    }

    /// <summary>Sets <paramref name="value"/> via <paramref name="setter"/> only if it differs
    /// from the current value (read via <paramref name="getter"/>), returning whether it changed
    /// -- the shared diff-then-write primitive every upsert method uses so an unchanged
    /// reconciler run never touches a row (SYNC-04's "run twice, zero delta" requirement).</summary>
    private static bool SetIfChanged<T>(Func<T> getter, Action<T> setter, T value)
    {
        if (Equals(getter(), value))
        {
            return false;
        }

        setter(value);
        return true;
    }

    private static ConnectionStatus ParseConnectionStatus(string status) => status.ToUpperInvariant() switch
    {
        "UPDATING" => ConnectionStatus.Updating,
        "UPDATED" => ConnectionStatus.Updated,
        "LOGIN_ERROR" => ConnectionStatus.LoginError,
        "WAITING_USER_INPUT" => ConnectionStatus.WaitingUserInput,
        "OUTDATED" => ConnectionStatus.Outdated,
        // Never throws on an unrecognized value (mirrors ItemStatusMapper's guidance) -- Outdated
        // is the safest fallback bucket (surfaces as "needs attention" rather than silently
        // looking healthy).
        _ => ConnectionStatus.Outdated,
    };

    private static AccountType ParseAccountType(string type) => type.ToUpperInvariant() switch
    {
        "BANK" => AccountType.Bank,
        "CREDIT" => AccountType.Credit,
        _ => AccountType.Bank,
    };

    private static AccountSubtype ParseAccountSubtype(string subtype) => subtype.ToUpperInvariant() switch
    {
        "CHECKING_ACCOUNT" => AccountSubtype.CheckingAccount,
        "SAVINGS_ACCOUNT" => AccountSubtype.SavingsAccount,
        "CREDIT_CARD" => AccountSubtype.CreditCard,
        _ => AccountSubtype.CheckingAccount,
    };

    private static CreditCardStatus ParseCreditCardStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "ACTIVE" => CreditCardStatus.Active,
        "BLOCKED" => CreditCardStatus.Blocked,
        "CANCELLED" => CreditCardStatus.Cancelled,
        _ => CreditCardStatus.Active,
    };

    private static InvestmentType ParseInvestmentType(string type) => type.ToUpperInvariant() switch
    {
        "MUTUAL_FUND" => InvestmentType.MutualFund,
        "FIXED_INCOME" => InvestmentType.FixedIncome,
        "EQUITY" => InvestmentType.Equity,
        "ETF" => InvestmentType.Etf,
        "SECURITY" => InvestmentType.Security,
        "COE" => InvestmentType.Coe,
        "CRYPTO" => InvestmentType.Crypto,
        _ => InvestmentType.Security,
    };

    private static TransactionType ParseTransactionType(string type) => type.ToUpperInvariant() switch
    {
        "DEBIT" => TransactionType.Debit,
        "CREDIT" => TransactionType.Credit,
        _ => TransactionType.Debit,
    };

    private static TransactionStatus ParseTransactionStatus(string status) => status.ToUpperInvariant() switch
    {
        "PENDING" => TransactionStatus.Pending,
        "POSTED" => TransactionStatus.Posted,
        _ => TransactionStatus.Posted,
    };

    private sealed class SyncCounts
    {
        public int AccountsAdded;
        public int AccountsUpdated;
        public int TransactionsAdded;
        public int TransactionsUpdated;
        public int InvestmentsAdded;
        public int InvestmentsUpdated;
    }
}

/// <summary>404-equivalent domain exception -- thrown when SyncConnectionAsync is called for a
/// Connection that doesn't exist or has been soft-deleted (excluded automatically by
/// SimpleFinanceDbContext's global soft-delete query filter). 02-07's endpoint layer maps this to
/// an HTTP 404.</summary>
public sealed class ConnectionNotFoundException(Guid connectionId)
    : Exception($"Connection '{connectionId}' was not found or has been removed.");
