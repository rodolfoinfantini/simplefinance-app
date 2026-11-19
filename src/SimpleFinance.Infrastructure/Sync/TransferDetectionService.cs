using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Sync;

/// <summary>
/// ITransferDetectionService implementation (TXN-05) -- the enrichment step
/// SyncConnectionUseCase runs immediately after every successful sync's main upsert save.
/// Deliberately placed in Infrastructure.Sync (not Application.Sync), mirroring
/// SyncConnectionUseCase's own documented placement (02-04-SUMMARY.md): it depends directly on
/// SimpleFinanceDbContext, and putting it in Application would require a circular
/// Application-&gt;Infrastructure project reference.
///
/// Layers two signals by strength over the user's currently-unflagged, non-overridden
/// transactions:
///   1. Pluggy paymentData (payer/receiver document match) -- the strongest signal, since it's
///      Pluggy's own semantic hint that money moved between two parties by document number.
///   2. Opposite-sign/same-|amount|/date-window fallback, only for pairs the paymentData pass
///      didn't already resolve -- conservative on purpose (exactly one candidate or no match),
///      per PITFALLS.md Pitfall 9's "never guess" mandate.
///
/// Both passes exclude IsTransferManualOverride=true rows entirely, so a user's explicit
/// PATCH .../transfer choice is permanently immune to being re-flipped by a later run. The
/// method is idempotent: once a pair is flagged IsTransfer=true, it's excluded from the next
/// run's candidate set, so re-running with unchanged data flags zero additional rows.
/// </summary>
public sealed class TransferDetectionService : ITransferDetectionService
{
    private const int DateToleranceDays = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SimpleFinanceDbContext _db;

    public TransferDetectionService(SimpleFinanceDbContext db)
    {
        _db = db;
    }

    public async Task<int> DetectTransfersForUserAsync(Guid userId, CancellationToken ct)
    {
        var candidates = await _db.Transactions
            .Where(t => t.UserId == userId && !t.IsTransfer && !t.IsTransferManualOverride)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var flaggedIds = new HashSet<Guid>();

        DetectByPaymentData(candidates, flaggedIds);
        DetectSameAccountReserves(candidates, flaggedIds);
        DetectByAmountDateFallback(candidates, flaggedIds);

        if (flaggedIds.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        foreach (var transaction in candidates.Where(t => flaggedIds.Contains(t.Id)))
        {
            transaction.IsTransfer = true;
            transaction.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        return flaggedIds.Count;
    }

    /// <summary>
    /// Primary signal: for each Debit candidate with a parseable, non-null
    /// <c>receiver.documentNumber</c>, look for a Credit candidate on a DIFFERENT account (same
    /// user) within the date window whose <c>payer.documentNumber</c> matches (case-insensitive)
    /// and whose |Amount| matches. First match wins -- paymentData document equality is already
    /// a strong, low-collision signal, unlike the fallback pass's amount/date-only heuristic.
    /// </summary>
    private static void DetectByPaymentData(List<Transaction> candidates, HashSet<Guid> flaggedIds)
    {
        var parsedPaymentData = candidates.ToDictionary(t => t.Id, t => TryParsePaymentData(t.PaymentDataJson));

        foreach (var debit in candidates.Where(t => t.Type == TransactionType.Debit))
        {
            if (flaggedIds.Contains(debit.Id))
            {
                continue;
            }

            var receiverDocument = parsedPaymentData[debit.Id]?.Receiver?.DocumentNumber;
            if (string.IsNullOrWhiteSpace(receiverDocument))
            {
                continue;
            }

            var dateFrom = debit.Date.AddDays(-DateToleranceDays);
            var dateTo = debit.Date.AddDays(DateToleranceDays);

            var match = candidates.FirstOrDefault(credit =>
                credit.Id != debit.Id
                && !flaggedIds.Contains(credit.Id)
                && credit.Type == TransactionType.Credit
                && credit.AccountId != debit.AccountId
                && credit.Date >= dateFrom && credit.Date <= dateTo
                && Math.Abs(credit.Amount) == Math.Abs(debit.Amount)
                && string.Equals(
                    parsedPaymentData[credit.Id]?.Payer?.DocumentNumber,
                    receiverDocument,
                    StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                flaggedIds.Add(debit.Id);
                flaggedIds.Add(match.Id);
            }
        }
    }

    /// <summary>
    /// Fallback signal: only runs over transactions the paymentData pass left unflagged. For each
    /// Debit candidate, finds Credit candidates on a different account (same user) with exactly
    /// matching |Amount| within the date window; flags the pair ONLY when exactly one such
    /// candidate exists. Zero or multiple candidates (ambiguous) -- never guess, mirrors
    /// SyncConnectionUseCase.ReconcileOneTransactionAsync's identical "ambiguous -> insert as
    /// new, never guess" precedent (02-04-SUMMARY.md, PITFALLS.md Pitfall 9).
    /// </summary>
    private static void DetectByAmountDateFallback(List<Transaction> candidates, HashSet<Guid> flaggedIds)
    {
        foreach (var debit in candidates.Where(t => t.Type == TransactionType.Debit))
        {
            if (flaggedIds.Contains(debit.Id))
            {
                continue;
            }

            var dateFrom = debit.Date.AddDays(-DateToleranceDays);
            var dateTo = debit.Date.AddDays(DateToleranceDays);

            var matches = candidates.Where(credit =>
                credit.Id != debit.Id
                && !flaggedIds.Contains(credit.Id)
                && credit.Type == TransactionType.Credit
                && credit.AccountId != debit.AccountId
                && credit.Date >= dateFrom && credit.Date <= dateTo
                && Math.Abs(credit.Amount) == Math.Abs(debit.Amount))
                .ToList();

            if (matches.Count == 1)
            {
                flaggedIds.Add(debit.Id);
                flaggedIds.Add(matches[0].Id);
            }
        }
    }

    /// <summary>
    /// Known accent-folded, lowercased keywords for same-account savings/reserve vaults
    /// (e.g. Mercado Pago Cofrinhos "Dinheiro retirado" / "Dinheiro reservado", Nubank Caixinhas,
    /// PicPay/Inter/Itaú Cofrinhos). Ordered most-specific first so same-day pairs of different
    /// operations (e.g. a R$ 500 "Dinheiro retirado" pair AND a R$ 500 "Dinheiro reservado" pair
    /// on the exact same date and account) pair unambiguously within their own family.
    /// </summary>
    private static readonly string[] SameAccountReserveFamilies =
    [
        "dinheiro retirado",
        "dinheiro reservado",
        "dinheiro guardado",
        "dinheiro resgatado",
        "resgate caixinha",
        "guardado na caixinha",
        "resgate da caixinha",
        "aplicacao rdb",
        "resgate rdb",
        "cofrinho",
        "caixinha",
    ];

    /// <summary>
    /// Third pass: detects internal reserve/vault movements posted as opposite-sign pairs on the
    /// SAME account and SAME calendar day (e.g. Mercado Pago Cofrinhos emitting
    /// "-500 Dinheiro retirado" + "+500 Dinheiro retirado Fatura" or
    /// "-500 Dinheiro reservado Emergência" + "+500 Dinheiro reservado").
    /// Requires both legs to share the same reserve keyword family so ordinary refunds on the same
    /// account are never falsely flagged.
    /// </summary>
    private static void DetectSameAccountReserves(List<Transaction> candidates, HashSet<Guid> flaggedIds)
    {
        var normalizedDesc = candidates.ToDictionary(t => t.Id, t => NormalizeDescription(t.Description));
        var familyById = candidates.ToDictionary(t => t.Id, t => ResolveReserveFamily(normalizedDesc[t.Id]));

        foreach (var debit in candidates.Where(t => t.Type == TransactionType.Debit))
        {
            if (flaggedIds.Contains(debit.Id))
            {
                continue;
            }

            var family = familyById[debit.Id];
            if (family is null)
            {
                continue;
            }

            var debitDesc = normalizedDesc[debit.Id];
            var debitDay = debit.Date.Date;
            var debitAmount = Math.Abs(debit.Amount);

            var sameFamilyCredits = candidates.Where(credit =>
                    credit.Id != debit.Id
                    && !flaggedIds.Contains(credit.Id)
                    && credit.Type == TransactionType.Credit
                    && credit.AccountId == debit.AccountId
                    && (credit.Date.Date == debitDay || Math.Abs((credit.Date - debit.Date).TotalMinutes) <= 10)
                    && Math.Abs(credit.Amount) == debitAmount
                    && string.Equals(familyById[credit.Id], family, StringComparison.Ordinal))
                .ToList();

            if (sameFamilyCredits.Count == 0)
            {
                continue;
            }

            // Prefer a pair where one description is a prefix of the other (e.g. "dinheiro retirado"
            // <-> "dinheiro retirado fatura"), then fall back to the first matching credit in the same family.
            var match = sameFamilyCredits.FirstOrDefault(c =>
                normalizedDesc[c.Id].StartsWith(debitDesc, StringComparison.Ordinal)
                || debitDesc.StartsWith(normalizedDesc[c.Id], StringComparison.Ordinal))
                ?? sameFamilyCredits[0];

            flaggedIds.Add(debit.Id);
            flaggedIds.Add(match.Id);
        }
    }

    private static string? ResolveReserveFamily(string normalizedDescription)
    {
        if (string.IsNullOrEmpty(normalizedDescription))
        {
            return null;
        }

        foreach (var family in SameAccountReserveFamilies)
        {
            if (normalizedDescription.Contains(family, StringComparison.Ordinal))
            {
                return family;
            }
        }

        return null;
    }

    private static string NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var decomposed = description.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
    }

    /// <summary>Never throws on malformed/unexpected-shape JSON (T-03-04's mitigation) -- returns
    /// null so the caller falls through to the fallback signal or stays unflagged.</summary>
    private static PluggyPaymentDataDto? TryParsePaymentData(string? paymentDataJson)
    {
        if (string.IsNullOrWhiteSpace(paymentDataJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PluggyPaymentDataDto>(paymentDataJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PluggyPaymentDataDto(PluggyPaymentPartyDto? Payer, PluggyPaymentPartyDto? Receiver);

    private sealed record PluggyPaymentPartyDto(string? DocumentNumber);
}
