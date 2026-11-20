using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Goals;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Alerts;

/// <summary>
/// NOTIF-02/NOTIF-03: the SOLE evaluator of active AlertRule rows into Notification rows +
/// dispatch -- a thin Hangfire-invoked adapter (mirrors NetWorthSnapshotJob/RecurringDetectionJob's
/// exact constructor-injection/ExecuteAsync shape) that iterates every User, evaluates each of
/// their active AlertRules against live data (budgets, account balances, card due dates,
/// transactions, recurring flags, connection health, goal completion), and fires a Notification +
/// NotificationDispatcher.DispatchAsync call for each condition that's newly true.
///
/// AlertFireLog-backed de-dupe (T-08-07, mitigate) is the load-bearing correctness guarantee here:
/// every evaluator's TargetKey is designed so re-running the job (hourly, or via the startup
/// backstop) can never re-fire an already-fired rule/target for the same natural period. See each
/// EvaluateXAsync method's own doc-comment for its exact key shape and why it's safe to re-scan.
///
/// Mirrors NetWorthSnapshotJob/RecurringDetectionJob's per-user isolation pattern verbatim
/// (T-06-06 precedent): one user's rule-evaluation failure (e.g. a malformed rule causing an
/// exception mid-evaluation) is caught, logged with structured UserId context, and never aborts
/// the batch -- every other user's rules must still be evaluated. db.ChangeTracker.Clear() on
/// catch discards that user's un-saved (Added/Modified) entities so they can never leak into and
/// re-fail the NEXT user's SaveChangesAsync call.
/// </summary>
public sealed class AlertEvaluationJob(
    SimpleFinanceDbContext db,
    BudgetService budgetService,
    GoalService goalService,
    NotificationDispatcher dispatcher,
    IOptions<AlertEvaluationOptions> options,
    ILogger<AlertEvaluationJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var users = await db.Users.ToListAsync(ct);

        foreach (var user in users)
        {
            try
            {
                var rules = await db.AlertRules
                    .Where(r => r.UserId == user.Id && r.IsActive)
                    .ToListAsync(ct);

                foreach (var rule in rules)
                {
                    switch (rule.Type)
                    {
                        case AlertRuleType.BudgetThreshold:
                            await EvaluateBudgetThresholdAsync(user, rule, ct);
                            break;
                        case AlertRuleType.LowBalance:
                            await EvaluateLowBalanceAsync(user, rule, ct);
                            break;
                        case AlertRuleType.InvoiceDue:
                            await EvaluateInvoiceDueAsync(user, rule, ct);
                            break;
                        case AlertRuleType.LargeTransaction:
                            await EvaluateLargeTransactionAsync(user, rule, ct);
                            break;
                        case AlertRuleType.NewRecurring:
                            await EvaluateNewRecurringAsync(user, rule, ct);
                            break;
                        case AlertRuleType.SyncError:
                            await EvaluateSyncErrorAsync(user, rule, ct);
                            break;
                        case AlertRuleType.GoalReached:
                            await EvaluateGoalReachedAsync(user, rule, ct);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                // T-06-06 precedent applied here: never let one user's failure abort the batch --
                // log structured context (UserId included) and move on. ChangeTracker.Clear()
                // discards this user's un-saved (Added/Modified) entities so they can never leak
                // into and re-fail the NEXT user's SaveChangesAsync call.
                logger.LogError(ex, "AlertEvaluationJob failed to evaluate alert rules for user {UserId}", user.Id);
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// BUD-04's 90%/100% budget-crossing tiers, reusing BudgetService.GetBudgetsForMonthAsync's
    /// exact live spend computation (never a second, divergent formula). Mutually exclusive per
    /// row per run: a >=100% row fires ONLY the 100 tier, never ALSO the 90 tier, since the
    /// Exceeded case supersedes the Attention case for the same category/month.
    /// </summary>
    private async Task EvaluateBudgetThresholdAsync(User user, AlertRule rule, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var budgets = await budgetService.GetBudgetsForMonthAsync(user.Id, now.Year, now.Month, ct);

        foreach (var row in budgets.Items)
        {
            if (rule.CategoryId is not null && row.CategoryId != rule.CategoryId)
            {
                continue;
            }

            var categoryKey = row.CategoryId?.ToString() ?? "global";
            var categoryName = row.CategoryName ?? "Overall";

            if (row.PercentOfCap >= 100m)
            {
                await FireAsync(
                    user,
                    rule,
                    $"{categoryKey}:{now.Year}-{now.Month}:100",
                    $"Budget exceeded (100%): {categoryName}",
                    $"{categoryName} has reached {FormatPercent(row.PercentOfCap)}% of its {FormatMoney(row.LimitAmount)} limit.",
                    ct);
            }
            else if (row.PercentOfCap >= 90m)
            {
                await FireAsync(
                    user,
                    rule,
                    $"{categoryKey}:{now.Year}-{now.Month}:90",
                    $"Budget alert (90%): {categoryName}",
                    $"{categoryName} has reached {FormatPercent(row.PercentOfCap)}% of its {FormatMoney(row.LimitAmount)} limit.",
                    ct);
            }
        }
    }

    /// <summary>
    /// De-duped once per account per calendar day -- balance can fluctuate above/below the
    /// threshold repeatedly through a day, and this key lets tomorrow's still-low balance alert
    /// again without spamming today.
    /// </summary>
    private async Task EvaluateLowBalanceAsync(User user, AlertRule rule, CancellationToken ct)
    {
        if (rule.Threshold is null)
        {
            return;
        }

        var accounts = await db.Accounts
            .Where(a => a.UserId == user.Id
                && !a.IsHidden
                && (rule.AccountId == null || a.Id == rule.AccountId)
                && a.Balance < rule.Threshold.Value)
            .ToListAsync(ct);

        foreach (var account in accounts)
        {
            await FireAsync(
                user,
                rule,
                $"{account.Id}:{DateTime.UtcNow.Date:yyyyMMdd}",
                $"Low balance: {account.Name}",
                $"{account.Name}'s balance is {FormatMoney(account.Balance)}, below your {FormatMoney(rule.Threshold.Value)} threshold.",
                ct);
        }
    }

    /// <summary>
    /// De-duped by (account, due date) -- a fresh key each billing cycle since the due date itself
    /// changes month to month, so next month's invoice can alert again.
    /// </summary>
    private async Task EvaluateInvoiceDueAsync(User user, AlertRule rule, CancellationToken ct)
    {
        var daysBefore = rule.DaysBefore ?? 3;
        var today = DateTime.UtcNow.Date;
        var horizon = today.AddDays(daysBefore);

        var dueCards = await (
            from ccd in db.CreditCardDetails
            join a in db.Accounts on ccd.AccountId equals a.Id
            where a.UserId == user.Id
                && !a.IsHidden
                && (rule.AccountId == null || a.Id == rule.AccountId)
                && ccd.BalanceDueDate != null
                && ccd.BalanceDueDate.Value.Date >= today
                && ccd.BalanceDueDate.Value.Date <= horizon
            select new { a.Id, a.Name, ccd.BalanceDueDate }
        ).ToListAsync(ct);

        foreach (var card in dueCards)
        {
            var dueDate = card.BalanceDueDate!.Value;
            await FireAsync(
                user,
                rule,
                $"{card.Id}:{dueDate:yyyyMMdd}",
                $"Invoice due soon: {card.Name}",
                $"{card.Name}'s invoice is due on {dueDate:yyyy-MM-dd}.",
                ct);
        }
    }

    /// <summary>
    /// De-duped by the transaction's own Id -- safe to re-scan the same lookback window on every
    /// run since a transaction can only ever fire once, ever, regardless of
    /// LargeTransactionLookbackDays after its first alert.
    /// </summary>
    private async Task EvaluateLargeTransactionAsync(User user, AlertRule rule, CancellationToken ct)
    {
        if (rule.Threshold is null)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-options.Value.LargeTransactionLookbackDays);

        var transactions = await db.Transactions
            .Where(t => t.UserId == user.Id
                && !t.IsTransfer
                && !t.IsHidden
                && t.Status == TransactionStatus.Posted
                && t.Date >= cutoff
                && (rule.AccountId == null || t.AccountId == rule.AccountId))
            .ToListAsync(ct);

        foreach (var txn in transactions.Where(t => Math.Abs(t.Amount) > rule.Threshold!.Value))
        {
            await FireAsync(
                user,
                rule,
                txn.Id.ToString(),
                "Large transaction detected",
                $"A transaction of {FormatMoney(Math.Abs(txn.Amount))} ({txn.Description}) was posted on {txn.Date:yyyy-MM-dd}.",
                ct);
        }
    }

    /// <summary>
    /// RECUR-04's "new recurring"/"amount increase" flags. A row can be both newly-detected AND
    /// increased in the same run -- fires twice, with two distinct de-dupe keys, since these are
    /// independent conditions. IsNewlyDetected fires once ever (`{id}:new`); AmountIncreaseDetected
    /// fires once per distinct increased amount (`{id}:increase:{amount}`) -- a LATER further
    /// increase changes the key, so it creates a second, new notification rather than being
    /// silently de-duped against the first increase alert.
    /// </summary>
    private async Task EvaluateNewRecurringAsync(User user, AlertRule rule, CancellationToken ct)
    {
        var recurringTransactions = await db.RecurringTransactions
            .Where(r => r.UserId == user.Id && r.IsActive && (r.IsNewlyDetected || r.AmountIncreaseDetected))
            .ToListAsync(ct);

        foreach (var r in recurringTransactions)
        {
            if (r.IsNewlyDetected)
            {
                await FireAsync(
                    user,
                    rule,
                    $"{r.Id}:new",
                    $"New recurring charge: {r.DisplayName}",
                    $"A new recurring charge of {FormatMoney(r.AverageAmount)} was detected: {r.DisplayName}.",
                    ct);
            }

            if (r.AmountIncreaseDetected)
            {
                await FireAsync(
                    user,
                    rule,
                    $"{r.Id}:increase:{r.AverageAmount:0.00}",
                    $"Recurring charge increased: {r.DisplayName}",
                    $"{r.DisplayName} increased from {FormatMoney(r.PreviousAverageAmount ?? 0m)} to {FormatMoney(r.AverageAmount)}.",
                    ct);
            }
        }
    }

    /// <summary>
    /// SYNC-*'s connection-health signal. De-duped once per (connection, status) per calendar day
    /// -- matches LowBalance's daily-refire cadence so a still-broken connection re-alerts the
    /// next day instead of going silent forever after the first alert.
    /// </summary>
    private async Task EvaluateSyncErrorAsync(User user, AlertRule rule, CancellationToken ct)
    {
        var connections = await db.Connections
            .Where(c => c.UserId == user.Id
                && (c.Status == ConnectionStatus.LoginError || c.Status == ConnectionStatus.Outdated))
            .ToListAsync(ct);

        foreach (var c in connections)
        {
            var connectionLabel = !string.IsNullOrWhiteSpace(c.CustomName)
                ? c.CustomName
                : (c.InstitutionName ?? c.ConnectorName ?? "your bank");
            await FireAsync(
                user,
                rule,
                $"{c.Id}:{c.Status}:{DateTime.UtcNow.Date:yyyyMMdd}",
                $"Connection issue: {connectionLabel}",
                $"Your connection to {connectionLabel} needs attention ({c.Status}).",
                ct);
        }
    }

    /// <summary>
    /// GOAL-03's completion signal. Calling GoalService.GetGoalsAsync performs the read-time
    /// Active->Completed reconciliation as a side effect -- this method never duplicates that
    /// logic, only reads the resulting Status. De-duped by `{goalId}:completed`, firing exactly
    /// once per goal's lifetime; a later archive/un-archive cycle is an accepted edge case, not
    /// handled specially (matches GOAL-03's own documented scope).
    /// </summary>
    private async Task EvaluateGoalReachedAsync(User user, AlertRule rule, CancellationToken ct)
    {
        var goals = await goalService.GetGoalsAsync(user.Id, ct);

        foreach (var g in goals.Where(g => g.Status == GoalStatus.Completed))
        {
            await FireAsync(
                user,
                rule,
                $"{g.Id}:completed",
                $"Goal reached: {g.Name}",
                $"Congratulations! You reached your goal \"{g.Name}\" of {FormatMoney(g.TargetAmount)}.",
                ct);
        }
    }

    /// <summary>
    /// Returns false (no-op) if an AlertFireLog row already exists for (ruleId, targetKey);
    /// otherwise inserts one (LastFiredAt = now) and returns true. Does NOT call
    /// SaveChangesAsync itself -- FireAsync does, in the same round-trip as the Notification
    /// insert, so both rows persist atomically together.
    /// </summary>
    private async Task<bool> TryRecordFireAsync(Guid userId, Guid ruleId, string targetKey, CancellationToken ct)
    {
        var alreadyFired = await db.AlertFireLogs
            .AnyAsync(f => f.AlertRuleId == ruleId && f.TargetKey == targetKey, ct);

        if (alreadyFired)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        db.AlertFireLogs.Add(new AlertFireLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AlertRuleId = ruleId,
            TargetKey = targetKey,
            LastFiredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        return true;
    }

    /// <summary>
    /// The shared fire path every evaluator calls: de-dupe check, Notification insert, one
    /// SaveChangesAsync round-trip covering both the AlertFireLog and Notification rows, then
    /// best-effort dispatch. DispatchAsync never throws (08-02's contract), so no try/catch is
    /// needed around that call specifically.
    /// </summary>
    private async Task FireAsync(
        User user, AlertRule rule, string targetKey, string title, string message, CancellationToken ct)
    {
        var recorded = await TryRecordFireAsync(user.Id, rule.Id, targetKey, ct);
        if (!recorded)
        {
            return;
        }

        var now = DateTime.UtcNow;
        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Type = rule.Type,
            Title = title,
            Message = message,
            Channel = rule.Channel,
            IsRead = false,
            SentAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        await dispatcher.DispatchAsync(rule.Channel, title, message, user.Email, ct);
    }

    // Matches ExportService.FormatAmount's existing "R$ 0.00"-style convention.
    private static string FormatMoney(decimal value) => "R$ " + value.ToString("0.00", CultureInfo.InvariantCulture);

    // Invariant-culture percent formatting -- current-culture ":0.0" would render a
    // comma decimal separator (e.g. "95,0") under pt-BR, breaking any exact-substring
    // assertion/consumer expecting a period.
    private static string FormatPercent(decimal value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
