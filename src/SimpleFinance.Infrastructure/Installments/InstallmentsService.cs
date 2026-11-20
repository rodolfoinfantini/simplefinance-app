using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Installments;

public sealed record InstallmentPlanItem(
    string Id,
    Guid AccountId,
    string AccountName,
    string? InstitutionName,
    string Description,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryIcon,
    string? CategoryColor,
    int CurrentInstallment,
    int TotalInstallments,
    int RemainingInstallments,
    decimal InstallmentAmount,
    decimal PaidAmount,
    decimal RemainingAmount,
    decimal TotalAmount,
    DateTime NextInstallmentDate,
    DateTime LastInstallmentDate);

public sealed record InstallmentsSummaryResponse(
    decimal TotalRemainingAmount,
    decimal MonthlyTotal,
    int ActiveCount,
    IReadOnlyList<InstallmentPlanItem> Items);

public sealed partial class InstallmentsService(SimpleFinanceDbContext db)
{
    [GeneratedRegex(@"\s+\d{1,2}/\d{1,2}\s*$", RegexOptions.Compiled)]
    private static partial Regex TrailingInstallmentSuffixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultipleWhitespaceRegex();

    public async Task<InstallmentsSummaryResponse> GetActiveInstallmentsAsync(
        Guid userId, Guid? accountId, CancellationToken ct)
    {
        var accountsQuery = db.Accounts
            .Where(a => a.UserId == userId);

        if (accountId is not null)
        {
            accountsQuery = accountsQuery.Where(a => a.Id == accountId.Value);
        }

        var accounts = await accountsQuery
            .GroupJoin(db.CreditCardDetails, a => a.Id, c => c.AccountId, (a, cards) => new { Account = a, Cards = cards })
            .SelectMany(x => x.Cards.DefaultIfEmpty(), (x, card) => new { x.Account, Card = card })
            .GroupJoin(db.Connections, x => x.Account.ConnectionId, conn => conn.Id, (x, conns) => new { x.Account, x.Card, Conns = conns })
            .SelectMany(x => x.Conns.DefaultIfEmpty(), (x, conn) => new
            {
                x.Account.Id,
                x.Account.Name,
                InstitutionName = conn == null
                    ? null
                    : (!string.IsNullOrWhiteSpace(conn.CustomName) ? conn.CustomName : conn.InstitutionName),
                BalanceCloseDate = x.Card != null ? x.Card.BalanceCloseDate : null,
            })
            .ToListAsync(ct);

        if (accounts.Count == 0)
        {
            return new InstallmentsSummaryResponse(0m, 0m, 0, []);
        }

        var accountById = accounts.ToDictionary(a => a.Id);
        var accountIds = accountById.Keys.ToList();

        var rows = await db.Transactions
            .Where(t => t.UserId == userId
                && accountIds.Contains(t.AccountId)
                && t.Type == TransactionType.Debit
                && !t.IsTransfer
                && !t.IsHidden
                && t.TotalInstallments != null
                && t.TotalInstallments > 1
                && t.InstallmentNumber != null
                && t.InstallmentNumber >= 1
                && t.InstallmentNumber <= t.TotalInstallments)
            .GroupJoin(db.Categories, t => t.CategoryId, c => c.Id, (t, cats) => new { Transaction = t, Cats = cats })
            .SelectMany(x => x.Cats.DefaultIfEmpty(), (x, cat) => new CandidateRow(
                x.Transaction.Id,
                x.Transaction.AccountId,
                x.Transaction.Date,
                x.Transaction.Description,
                Math.Abs(x.Transaction.Amount),
                x.Transaction.Status,
                x.Transaction.BillId,
                x.Transaction.InstallmentNumber!.Value,
                x.Transaction.TotalInstallments!.Value,
                x.Transaction.CategoryId,
                cat != null ? cat.Name : null,
                cat != null ? cat.Icon : null,
                cat != null ? cat.Color : null))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return new InstallmentsSummaryResponse(0m, 0m, 0, []);
        }

        var plans = GroupIntoPlans(rows);
        var activeItems = new List<InstallmentPlanItem>();
        var fallbackUnpaidCutoff = DateTime.UtcNow.Date.AddDays(-35);

        foreach (var plan in plans)
        {
            var accountInfo = accountById[plan.AccountId];
            var unpaidCycleStartUtc = accountInfo.BalanceCloseDate is not null
                ? DateTime.SpecifyKind(accountInfo.BalanceCloseDate.Value.Date.AddMonths(-1), DateTimeKind.Utc).AddHours(6)
                : fallbackUnpaidCutoff;

            var orderedInstallments = plan.Rows.Values
                .OrderBy(r => r.InstallmentNumber)
                .ToList();

            var latestRow = orderedInstallments[^1];
            var maxInstallmentNumber = latestRow.InstallmentNumber;
            var totalInstallments = plan.TotalInstallments;

            // Installments already present in the DB that are still unpaid (Pending, no BillId, and within/after the current unpaid billing cycle).
            var unpaidDbRows = orderedInstallments
                .Where(r => r.Status == TransactionStatus.Pending
                    && r.BillId == null
                    && r.Date > unpaidCycleStartUtc)
                .ToList();

            // Some connectors (e.g. Sicoob, NuBank) only insert one row per billing cycle and do not pre-create future months' rows.
            // Project any installments > maxInstallmentNumber month-by-month from latestRow.Date.
            var projectedFutureInstallments = new List<(int InstallmentNumber, DateTime Date, decimal Amount)>();
            if (maxInstallmentNumber < totalInstallments)
            {
                for (var num = maxInstallmentNumber + 1; num <= totalInstallments; num++)
                {
                    var monthsAhead = num - maxInstallmentNumber;
                    var projectedDate = latestRow.Date.AddMonths(monthsAhead);
                    if (projectedDate > unpaidCycleStartUtc)
                    {
                        projectedFutureInstallments.Add((num, projectedDate, latestRow.Amount));
                    }
                }
            }

            if (unpaidDbRows.Count == 0 && projectedFutureInstallments.Count == 0)
            {
                continue;
            }

            var firstUnpaidNumber = unpaidDbRows.Count > 0
                ? unpaidDbRows[0].InstallmentNumber
                : projectedFutureInstallments[0].InstallmentNumber;

            var nextInstallmentDate = unpaidDbRows.Count > 0
                ? unpaidDbRows[0].Date
                : projectedFutureInstallments[0].Date;

            var lastInstallmentDate = projectedFutureInstallments.Count > 0
                ? projectedFutureInstallments[^1].Date
                : unpaidDbRows[^1].Date;

            var installmentAmount = unpaidDbRows.Count > 0
                ? unpaidDbRows[0].Amount
                : latestRow.Amount;

            var remainingInstallments = unpaidDbRows.Count + projectedFutureInstallments.Count;
            var remainingAmount = unpaidDbRows.Sum(r => r.Amount) + projectedFutureInstallments.Sum(p => p.Amount);

            var paidCount = Math.Max(0, firstUnpaidNumber - 1);
            var paidDbRows = orderedInstallments
                .Where(r => r.InstallmentNumber < firstUnpaidNumber)
                .ToList();
            var paidAmount = paidDbRows.Count == paidCount
                ? paidDbRows.Sum(r => r.Amount)
                : paidDbRows.Sum(r => r.Amount) + Math.Max(0, paidCount - paidDbRows.Count) * installmentAmount;

            var displayRow = orderedInstallments
                .OrderByDescending(r => CleanDisplayDescription(r.Description).Length)
                .First();
            var categorizedRow = orderedInstallments.FirstOrDefault(r => r.CategoryId is not null) ?? displayRow;

            activeItems.Add(new InstallmentPlanItem(
                Id: displayRow.Id.ToString(),
                AccountId: plan.AccountId,
                AccountName: accountInfo.Name,
                InstitutionName: accountInfo.InstitutionName,
                Description: CleanDisplayDescription(displayRow.Description),
                CategoryId: categorizedRow.CategoryId,
                CategoryName: categorizedRow.CategoryName,
                CategoryIcon: categorizedRow.CategoryIcon,
                CategoryColor: categorizedRow.CategoryColor,
                CurrentInstallment: firstUnpaidNumber,
                TotalInstallments: totalInstallments,
                RemainingInstallments: remainingInstallments,
                InstallmentAmount: installmentAmount,
                PaidAmount: paidAmount,
                RemainingAmount: remainingAmount,
                TotalAmount: paidAmount + remainingAmount,
                NextInstallmentDate: nextInstallmentDate,
                LastInstallmentDate: lastInstallmentDate));
        }

        var sortedItems = activeItems
            .OrderByDescending(i => i.RemainingAmount)
            .ThenByDescending(i => i.LastInstallmentDate)
            .ToList();

        var totalRemaining = sortedItems.Sum(i => i.RemainingAmount);
        var monthlyTotal = sortedItems.Sum(i => i.InstallmentAmount);

        return new InstallmentsSummaryResponse(
            TotalRemainingAmount: totalRemaining,
            MonthlyTotal: monthlyTotal,
            ActiveCount: sortedItems.Count,
            Items: sortedItems);
    }

    private static List<InstallmentPlanGroup> GroupIntoPlans(List<CandidateRow> rows)
    {
        var plans = new List<InstallmentPlanGroup>();

        foreach (var row in rows
            .OrderBy(r => r.AccountId)
            .ThenBy(r => r.TotalInstallments)
            .ThenBy(r => r.InstallmentNumber)
            .ThenBy(r => r.Date))
        {
            var rawKey = NormalizeRawKey(row.Description);
            var collapsedKey = CollapseWhitespace(rawKey);

            var match = plans.FirstOrDefault(p =>
                p.AccountId == row.AccountId
                && p.TotalInstallments == row.TotalInstallments
                && !p.Rows.ContainsKey(row.InstallmentNumber)
                && Math.Abs(p.ReferenceAmount - row.Amount) <= 0.50m
                && DescriptionsMatch(p.RawKey, p.CollapsedKey, rawKey, collapsedKey)
                && IsChronologicallyConsistent(p, row));

            if (match is null)
            {
                var group = new InstallmentPlanGroup(row.AccountId, row.TotalInstallments, rawKey, collapsedKey, row.Amount);
                group.Rows[row.InstallmentNumber] = row;
                plans.Add(group);
            }
            else
            {
                match.Rows[row.InstallmentNumber] = row;
                if (rawKey.Length > match.RawKey.Length)
                {
                    match.RawKey = rawKey;
                }
                if (collapsedKey.Length > match.CollapsedKey.Length)
                {
                    match.CollapsedKey = collapsedKey;
                }
            }
        }

        return plans;
    }

    private static bool DescriptionsMatch(string rawA, string collapsedA, string rawB, string collapsedB)
    {
        if (string.Equals(collapsedA, collapsedB, StringComparison.Ordinal))
        {
            return true;
        }

        // Mercado Pago truncates future-month installment descriptions to 16-17 raw characters
        // (e.g. "BRUDOVAN PNEUS LT" vs "BRUDOVAN PNEUS LTDA", "APP         *KUB" vs "APP         *KUBAAUDIO").
        var minRawLen = Math.Min(rawA.Length, rawB.Length);
        if (minRawLen >= 14 && (rawA.StartsWith(rawB, StringComparison.Ordinal) || rawB.StartsWith(rawA, StringComparison.Ordinal)))
        {
            return true;
        }

        var minCollapsedLen = Math.Min(collapsedA.Length, collapsedB.Length);
        if (minCollapsedLen >= 12
            && (collapsedA.StartsWith(collapsedB, StringComparison.Ordinal) || collapsedB.StartsWith(collapsedA, StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static bool IsChronologicallyConsistent(InstallmentPlanGroup plan, CandidateRow candidate)
    {
        foreach (var existing in plan.Rows.Values)
        {
            if (existing.InstallmentNumber < candidate.InstallmentNumber
                && candidate.Date < existing.Date.AddDays(-5))
            {
                return false;
            }

            if (existing.InstallmentNumber > candidate.InstallmentNumber
                && candidate.Date > existing.Date.AddDays(5))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeRawKey(string description)
    {
        var stripped = TrailingInstallmentSuffixRegex().Replace(description.Trim(), string.Empty);
        return stripped.Trim().ToUpperInvariant();
    }

    private static string CollapseWhitespace(string input) =>
        MultipleWhitespaceRegex().Replace(input, " ").Trim();

    private static string CleanDisplayDescription(string description)
    {
        var stripped = TrailingInstallmentSuffixRegex().Replace(description.Trim(), string.Empty);
        return MultipleWhitespaceRegex().Replace(stripped, " ").Trim();
    }

    private sealed record CandidateRow(
        Guid Id,
        Guid AccountId,
        DateTime Date,
        string Description,
        decimal Amount,
        TransactionStatus Status,
        string? BillId,
        int InstallmentNumber,
        int TotalInstallments,
        Guid? CategoryId,
        string? CategoryName,
        string? CategoryIcon,
        string? CategoryColor);

    private sealed class InstallmentPlanGroup(
        Guid accountId,
        int totalInstallments,
        string rawKey,
        string collapsedKey,
        decimal referenceAmount)
    {
        public Guid AccountId { get; } = accountId;
        public int TotalInstallments { get; } = totalInstallments;
        public string RawKey { get; set; } = rawKey;
        public string CollapsedKey { get; set; } = collapsedKey;
        public decimal ReferenceAmount { get; } = referenceAmount;
        public Dictionary<int, CandidateRow> Rows { get; } = [];
    }
}
