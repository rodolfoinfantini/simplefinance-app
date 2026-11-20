using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Investments;

public sealed record InvestmentPositionResult(
    Guid Id, string Name, string Type, string? Subtype, string? Institution, string? IssuerName,
    decimal? Value, decimal? AmountInvested, decimal? Gain, decimal? GainPercent,
    decimal? AnnualRate, decimal? LastMonthRate, DateTime? Date, DateTime? DueDate, string? CurrencyCode);

public sealed record InvestmentPositionsResult(IReadOnlyList<InvestmentPositionResult> Items);

public sealed record InvestmentDistributionItem(string Label, decimal Value, decimal Percent);

public sealed record InvestmentsSummaryResult(
    decimal TotalInvested, decimal TotalCurrentValue, decimal TotalGain, decimal? TotalGainPercent,
    IReadOnlyList<InvestmentDistributionItem> ByType, IReadOnlyList<InvestmentDistributionItem> ByIssuer);

/// <summary>
/// The real, directly-testable Investments business logic (06-03 Task 1) -- INV-01/INV-02's
/// per-position gain/gain-percent and per-summary total-gain/distribution-percent computation,
/// tolerant of every null/zero case 06-02's InvestmentQueries already proved safe at the query
/// layer. InvestmentsEndpoints.cs (Task 2) is a thin HTTP wrapper over this class only -- no
/// business logic duplicated there.
/// </summary>
public sealed class InvestmentsService(InvestmentQueries investmentQueries) : IInvestmentsReader
{
    // Human display label per InvestmentType -- the SAME map is reused for both
    // InvestmentPositionResult.Type and InvestmentDistributionItem.Label in ByType (never diverge
    // the two), per the plan's interfaces block.
    private static readonly Dictionary<InvestmentType, string> TypeLabels = new()
    {
        [InvestmentType.MutualFund] = "Mutual Fund",
        [InvestmentType.FixedIncome] = "Fixed Income",
        [InvestmentType.Equity] = "Equity",
        [InvestmentType.Etf] = "ETF",
        [InvestmentType.Security] = "Security",
        [InvestmentType.Coe] = "COE",
        [InvestmentType.Crypto] = "Crypto",
    };

    /// <summary>
    /// INV-01: every position, gain/gain-percent computed per row -- Gain = Value - AmountInvested
    /// only when both are non-null (else null); GainPercent = Gain / AmountInvested * 100 only
    /// when Gain is non-null AND AmountInvested != 0 (else null, avoids divide-by-zero and a
    /// nonsensical percent when nothing was invested).
    /// </summary>
    public async Task<InvestmentPositionsResult> GetPositionsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await investmentQueries.GetPositionsAsync(userId, ct);

        var items = rows.Select(row =>
        {
            var gain = row.Value is not null && row.AmountInvested is not null
                ? row.Value.Value - row.AmountInvested.Value
                : (decimal?)null;

            var gainPercent = gain is not null && row.AmountInvested != 0
                ? gain.Value / row.AmountInvested!.Value * 100
                : (decimal?)null;

            return new InvestmentPositionResult(
                row.Id, row.Name, TypeLabels[row.Type], row.Subtype, row.Institution, row.IssuerName,
                row.Value, row.AmountInvested, gain, gainPercent,
                row.AnnualRate, row.LastMonthRate, row.Date, row.DueDate, row.CurrencyCode);
        }).ToList();

        return new InvestmentPositionsResult(items);
    }

    /// <summary>
    /// INV-02: totals + distribution-by-type/issuer. TotalGain always computed (TotalCurrentValue
    /// - TotalInvested is never null since InvestmentQueries.GetTotalsAsync already COALESCEs both
    /// to 0). TotalGainPercent is null when TotalInvested is 0 (zero-safe, mirrors
    /// DashboardService's CalculatePercent convention). Each distribution item's Percent is
    /// relative to TotalCurrentValue, 0 (not null) when TotalCurrentValue is 0 -- matches
    /// DashboardService.GetSpendingByCategoryAsync's zero-safe precedent, since a chart needs a
    /// plottable number even in the zero-total edge case.
    /// </summary>
    public async Task<InvestmentsSummaryResult> GetSummaryAsync(Guid userId, CancellationToken ct)
    {
        var totals = await investmentQueries.GetTotalsAsync(userId, ct);
        var byTypeRows = await investmentQueries.GetDistributionByTypeAsync(userId, ct);
        var byIssuerRows = await investmentQueries.GetDistributionByIssuerAsync(userId, ct);

        var totalGain = totals.TotalGain;
        var totalGainPercent = totals.TotalInvested != 0
            ? totalGain / totals.TotalInvested * 100
            : (decimal?)null;

        var byType = byTypeRows
            .Select(row => new InvestmentDistributionItem(TypeLabels[row.Type], row.Value, DistributionPercent(row.Value, totals.TotalCurrentValue)))
            .ToList();

        var byIssuer = byIssuerRows
            .Select(row => new InvestmentDistributionItem(row.IssuerLabel, row.Value, DistributionPercent(row.Value, totals.TotalCurrentValue)))
            .ToList();

        return new InvestmentsSummaryResult(
            totals.TotalInvested, totals.TotalCurrentValue, totalGain, totalGainPercent, byType, byIssuer);
    }

    private static decimal DistributionPercent(decimal value, decimal totalCurrentValue) =>
        totalCurrentValue != 0 ? value / totalCurrentValue * 100 : 0;
}
