using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Budgets;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Dashboard;

public sealed record DashboardSummaryResult(
    decimal NetWorth, decimal TotalBalance, decimal TotalCardDebt,
    decimal PeriodIncome, decimal PeriodExpense, decimal PeriodNet);

public sealed record SpendingByCategoryItemResult(
    Guid? CategoryId, string CategoryName, string? CategoryIcon, string? CategoryColor, decimal Amount, decimal Percent);

public sealed record SpendingByCategoryResult(IReadOnlyList<SpendingByCategoryItemResult> Items, decimal TotalAmount);

public sealed record IncomeExpenseMonthResult(int Year, int Month, decimal Income, decimal Expense);

public sealed record IncomeExpenseResult(IReadOnlyList<IncomeExpenseMonthResult> Items);

public sealed record NetWorthPointResult(DateTime Date, decimal NetWorth);

public sealed record NetWorthEvolutionResult(IReadOnlyList<NetWorthPointResult> Points);

public sealed record AtRiskBudgetItemResult(
    Guid CategoryId, string CategoryName, string? CategoryIcon, string? CategoryColor,
    decimal PercentOfCap, decimal ProjectedPercent, BudgetState State);

public sealed record AtRiskBudgetsResult(IReadOnlyList<AtRiskBudgetItemResult> Items);

public sealed record UpcomingDueDateItemResult(
    Guid AccountId, string? InstitutionName, string AccountName, DateTime DueDate, decimal? StatementBalance);

public sealed record UpcomingDueDatesResult(IReadOnlyList<UpcomingDueDateItemResult> Items);

public sealed record SpendingPaceDayPoint(
    int Day, decimal? CurrentMonth, decimal PreviousMonth, decimal ThreeMonthAverage);

public sealed record SpendingPaceResult(
    int CurrentDay,
    decimal CurrentMonthTotal,
    decimal PreviousMonthAtSameDay,
    decimal ThreeMonthAverageAtSameDay,
    IReadOnlyList<SpendingPaceDayPoint> Points);

public sealed record TrendMonth(int Year, int Month);

public sealed record ParentCategoryTrendSeries(
    Guid? CategoryId, string CategoryName, string? CategoryColor, IReadOnlyList<decimal> Amounts);

public sealed record ParentCategoryTrendResult(
    IReadOnlyList<TrendMonth> Months, IReadOnlyList<ParentCategoryTrendSeries> Series);

/// <summary>
/// DASH-01..05: the Dashboard's full read surface (05-06). Every actual SQL/aggregation call is
/// delegated to <see cref="DashboardQueries"/> (05-04, Dapper) or, for at-risk budgets,
/// <see cref="BudgetCalculator"/> (05-02) -- this class only re-shapes/ranks/gap-fills the results
/// it's handed, it never issues its own SQL. Placed in Infrastructure (not Application), mirroring
/// BudgetService's (05-05) documented rationale: it depends directly on both
/// <see cref="SimpleFinanceDbContext"/> and DashboardQueries, and Application cannot reference
/// Infrastructure without a circular project reference.
///
/// dateFrom/dateTo/rangeStart/rangeEnd are always inclusive on both ends, matching
/// DashboardQueries.cs's and 03-03-SUMMARY.md's established convention.
/// </summary>
public sealed class DashboardService(SimpleFinanceDbContext db, DashboardQueries dashboardQueries)
{
    /// <summary>DASH-01: a thin pass-through to DashboardQueries.GetTotalsAsync, adding PeriodNet
    /// (the only field DashboardQueries itself doesn't compute).</summary>
    public async Task<DashboardSummaryResult> GetSummaryAsync(
        Guid userId, DateTime dateFrom, DateTime dateTo, CancellationToken ct)
    {
        var totals = await dashboardQueries.GetTotalsAsync(userId, dateFrom, dateTo, ct);

        return new DashboardSummaryResult(
            totals.NetWorth,
            totals.TotalBalance,
            totals.TotalCardDebt,
            totals.PeriodIncome,
            totals.PeriodExpense,
            totals.PeriodIncome - totals.PeriodExpense);
    }

    /// <summary>DASH-02: ranks every group DashboardQueries.GetCategorySpendAsync returns
    /// (including the CategoryId=null uncategorized group) by Amount descending, takes the top 5
    /// as-is (renaming a CategoryId=null group among them to "Uncategorized"), and aggregates any
    /// remaining groups beyond the top 5 into a single synthetic trailing "Others" item.
    /// TotalAmount always sums EVERY returned group, not just the top 5.</summary>
    public async Task<SpendingByCategoryResult> GetSpendingByCategoryAsync(
        Guid userId, DateTime dateFrom, DateTime dateTo, CancellationToken ct)
    {
        var rows = await dashboardQueries.GetCategorySpendAsync(userId, dateFrom, dateTo, ct);

        // DashboardQueries already orders by Amount DESC, but re-assert here -- this method's
        // correctness must never depend on the caller's internal ordering implementation detail.
        var ordered = rows.OrderByDescending(r => r.Amount).ToList();
        var totalAmount = ordered.Sum(r => r.Amount);

        var top5 = ordered.Take(5).ToList();
        var remainder = ordered.Skip(5).ToList();

        var items = top5
            .Select(r => new SpendingByCategoryItemResult(
                r.CategoryId,
                r.CategoryId is null ? "Uncategorized" : r.CategoryName ?? "Uncategorized",
                r.CategoryIcon,
                r.CategoryColor,
                r.Amount,
                CalculatePercent(r.Amount, totalAmount)))
            .ToList();

        if (remainder.Count > 0)
        {
            var othersAmount = remainder.Sum(r => r.Amount);
            items.Add(new SpendingByCategoryItemResult(
                null, "Others", null, null, othersAmount, CalculatePercent(othersAmount, totalAmount)));
        }

        return new SpendingByCategoryResult(items, totalAmount);
    }

    /// <summary>Aggregates spending rolled up to parent categories (combining each parent category
    /// with all of its child subcategories).</summary>
    public async Task<SpendingByCategoryResult> GetSpendingByParentCategoryAsync(
        Guid userId, DateTime dateFrom, DateTime dateTo, CancellationToken ct)
    {
        var rows = await dashboardQueries.GetParentCategorySpendAsync(userId, dateFrom, dateTo, ct);

        var ordered = rows.OrderByDescending(r => r.Amount).ToList();
        var totalAmount = ordered.Sum(r => r.Amount);

        var items = ordered
            .Select(r => new SpendingByCategoryItemResult(
                r.CategoryId,
                r.CategoryId is null ? "Uncategorized" : r.CategoryName ?? "Uncategorized",
                r.CategoryIcon,
                r.CategoryColor,
                r.Amount,
                CalculatePercent(r.Amount, totalAmount)))
            .ToList();

        return new SpendingByCategoryResult(items, totalAmount);
    }

    /// <summary>DASH-03: computes rangeStart (first day of the month monthsBack-1 months before
    /// the current UTC month) / rangeEnd (last day of the current UTC month), calls
    /// DashboardQueries.GetIncomeExpenseByMonthAsync, then gap-fills every (year, month) in the
    /// requested range with no matching row so the response always has exactly monthsBack items in
    /// chronological order -- the frontend chart must never receive a sparse/reordered series.</summary>
    public async Task<IncomeExpenseResult> GetIncomeExpenseAsync(Guid userId, int monthsBack, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var rangeStartMonth = currentMonthStart.AddMonths(-(monthsBack - 1));
        var rangeEnd = new DateTime(
            currentMonthStart.Year, currentMonthStart.Month,
            DateTime.DaysInMonth(currentMonthStart.Year, currentMonthStart.Month), 23, 59, 59, DateTimeKind.Utc);

        var rows = await dashboardQueries.GetIncomeExpenseByMonthAsync(userId, rangeStartMonth, rangeEnd, ct);
        var byMonth = rows.ToDictionary(r => (r.Year, r.Month));

        var items = new List<IncomeExpenseMonthResult>(monthsBack);
        for (var i = 0; i < monthsBack; i++)
        {
            var month = rangeStartMonth.AddMonths(i);
            items.Add(byMonth.TryGetValue((month.Year, month.Month), out var row)
                ? new IncomeExpenseMonthResult(month.Year, month.Month, row.Income, row.Expense)
                : new IncomeExpenseMonthResult(month.Year, month.Month, 0m, 0m));
        }

        return new IncomeExpenseResult(items);
    }

    /// <summary>DASH-03: computes rangeStart (today minus monthsBack months, 6 or 12), calls
    /// DashboardQueries.GetNetWorthEvolutionAsync, maps rows as-is -- NO gap-filling here.
    /// 05-UI-SPEC.md's 0/1/2+-point rendering is explicitly a FRONTEND concern; this method just
    /// returns whatever real snapshot rows exist, including zero, and is proven safe to call
    /// before Phase 6's INV-04 job ever runs.</summary>
    public async Task<NetWorthEvolutionResult> GetNetWorthEvolutionAsync(Guid userId, int monthsBack, CancellationToken ct)
    {
        var rangeStart = DateTime.UtcNow.AddMonths(-monthsBack);
        var rows = await dashboardQueries.GetNetWorthEvolutionAsync(userId, rangeStart, ct);
        var points = rows.Select(r => new NetWorthPointResult(r.Date, r.NetWorth)).ToList();
        return new NetWorthEvolutionResult(points);
    }

    /// <summary>DASH-04: always the CURRENT UTC month/year -- budgets are inherently monthly, so
    /// this never accepts a caller-supplied period. Loads every Budget row with a non-null
    /// CategoryId (the global cap never appears in the dashboard's per-category at-risk list),
    /// computes spend via DashboardQueries.GetCategorySpendAsync, runs BudgetCalculator.Calculate
    /// per row (the exact same math 05-05's BudgetService uses), and includes a row ONLY when
    /// IsAtRisk || IsProjectedToExceed is true.</summary>
    public async Task<AtRiskBudgetsResult> GetAtRiskBudgetsAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var year = now.Year;
        var month = now.Month;

        var budgets = await db.Budgets
            .Where(b => b.UserId == userId && b.Year == year && b.Month == month && b.CategoryId != null)
            .ToListAsync(ct);

        if (budgets.Count == 0)
        {
            return new AtRiskBudgetsResult([]);
        }

        var periodStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59, DateTimeKind.Utc);
        var spendRows = await dashboardQueries.GetCategorySpendAsync(userId, periodStart, periodEnd, ct);

        var categoryIds = budgets.Select(b => b.CategoryId!.Value).Distinct().ToList();
        var categoriesById = await db.Categories
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var dayOfMonth = now.Day;
        var daysInMonth = DateTime.DaysInMonth(year, month);

        var items = new List<AtRiskBudgetItemResult>();
        foreach (var budget in budgets)
        {
            var spentAmount = spendRows.FirstOrDefault(r => r.CategoryId == budget.CategoryId)?.Amount ?? 0m;
            var progress = BudgetCalculator.Calculate(budget.LimitAmount, spentAmount, dayOfMonth, daysInMonth);

            if (!progress.IsAtRisk && !progress.IsProjectedToExceed)
            {
                continue;
            }

            categoriesById.TryGetValue(budget.CategoryId!.Value, out var category);

            items.Add(new AtRiskBudgetItemResult(
                budget.CategoryId!.Value,
                category?.Name ?? "Unknown",
                category?.Icon,
                category?.Color,
                progress.PercentOfCap,
                progress.ProjectedPercent,
                progress.State));
        }

        return new AtRiskBudgetsResult(items);
    }

    /// <summary>DASH-04: EF Core LINQ (not Dapper -- this is a filtered join, not an aggregate,
    /// per 05-CONTEXT.md's Dapper-for-aggregates scoping), joining CreditCardDetail -> Account ->
    /// Connection exactly per AccountsEndpoints.cs's GroupJoin/SelectMany pattern. Filtered to
    /// BalanceDueDate within [today, today+7] inclusive, the caller's non-hidden accounts only,
    /// ordered ascending by BalanceDueDate.</summary>
    public async Task<UpcomingDueDatesResult> GetUpcomingDueDatesAsync(Guid userId, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var windowEnd = today.AddDays(7);

        var rows = await db.CreditCardDetails
            .Where(c => c.BalanceDueDate != null && c.BalanceDueDate >= today && c.BalanceDueDate <= windowEnd)
            .GroupJoin(db.Accounts, c => c.AccountId, a => a.Id, (c, accounts) => new { Card = c, Accounts = accounts })
            .SelectMany(x => x.Accounts, (x, account) => new { x.Card, Account = account })
            .Where(x => x.Account.UserId == userId && x.Account.IsHidden == false)
            .GroupJoin(db.Connections, x => x.Account.ConnectionId, conn => conn.Id, (x, conns) => new { x.Card, x.Account, Conns = conns })
            .SelectMany(x => x.Conns.DefaultIfEmpty(), (x, conn) => new { x.Card, x.Account, Connection = conn })
            .OrderBy(x => x.Card.BalanceDueDate)
            .ToListAsync(ct);

        var items = rows
            .Select(x => new UpcomingDueDateItemResult(
                x.Account.Id,
                x.Connection == null
                    ? null
                    : (!string.IsNullOrWhiteSpace(x.Connection.CustomName) ? x.Connection.CustomName : x.Connection.InstitutionName),
                x.Account.Name,
                x.Card.BalanceDueDate!.Value,
                x.Card.LastStatementBalance ?? x.Account.Balance))
            .ToList();

        return new UpcomingDueDatesResult(items);
    }

    /// <summary>Cumulative day-by-day expense burn-up curve for the current month compared against
    /// the prior month and the 3-month rolling average (computed in America/Sao_Paulo).</summary>
    public async Task<SpendingPaceResult> GetSpendingPaceAsync(Guid userId, CancellationToken ct)
    {
        var todayBrt = DateTime.UtcNow.AddHours(-3).Date;
        var currentMonthStart = new DateTime(todayBrt.Year, todayBrt.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var prev1 = currentMonthStart.AddMonths(-1);
        var prev2 = currentMonthStart.AddMonths(-2);
        var prev3 = currentMonthStart.AddMonths(-3);

        // Widen UTC bounds by +/- 1 day so BRT timezone conversion at month boundaries never clips.
        var rangeStart = prev3.AddDays(-1);
        var rangeEnd = new DateTime(
            todayBrt.Year, todayBrt.Month,
            DateTime.DaysInMonth(todayBrt.Year, todayBrt.Month), 23, 59, 59, DateTimeKind.Utc).AddDays(1);

        var rows = await dashboardQueries.GetDailyExpenseAsync(userId, rangeStart, rangeEnd, ct);
        var dailyMap = rows.ToDictionary(r => (r.Year, r.Month, r.Day), r => r.Expense);

        var daysInCurrentMonth = DateTime.DaysInMonth(todayBrt.Year, todayBrt.Month);
        var currentDay = todayBrt.Day;

        var curCumulative = BuildCumulativeSeries(dailyMap, currentMonthStart.Year, currentMonthStart.Month, daysInCurrentMonth);
        var prev1Cumulative = BuildCumulativeSeries(dailyMap, prev1.Year, prev1.Month, daysInCurrentMonth);
        var prev2Cumulative = BuildCumulativeSeries(dailyMap, prev2.Year, prev2.Month, daysInCurrentMonth);
        var prev3Cumulative = BuildCumulativeSeries(dailyMap, prev3.Year, prev3.Month, daysInCurrentMonth);

        var points = new List<SpendingPaceDayPoint>(daysInCurrentMonth);
        for (var d = 1; d <= daysInCurrentMonth; d++)
        {
            var avg = Math.Round((prev1Cumulative[d] + prev2Cumulative[d] + prev3Cumulative[d]) / 3m, 2);
            points.Add(new SpendingPaceDayPoint(
                Day: d,
                CurrentMonth: d <= currentDay ? curCumulative[d] : null,
                PreviousMonth: prev1Cumulative[d],
                ThreeMonthAverage: avg));
        }

        var todayPoint = points[currentDay - 1];
        return new SpendingPaceResult(
            CurrentDay: currentDay,
            CurrentMonthTotal: todayPoint.CurrentMonth ?? 0m,
            PreviousMonthAtSameDay: todayPoint.PreviousMonth,
            ThreeMonthAverageAtSameDay: todayPoint.ThreeMonthAverage,
            Points: points);
    }

    private static decimal[] BuildCumulativeSeries(
        Dictionary<(int Year, int Month, int Day), decimal> dailyMap,
        int year,
        int month,
        int targetDays)
    {
        var daysInSourceMonth = DateTime.DaysInMonth(year, month);
        var cumulative = new decimal[targetDays + 1];
        var running = 0m;
        for (var d = 1; d <= targetDays; d++)
        {
            if (d <= daysInSourceMonth && dailyMap.TryGetValue((year, month, d), out var amount))
            {
                running += amount;
            }
            cumulative[d] = running;
        }
        return cumulative;
    }

    /// <summary>Monthly spending evolution rolled up by parent category across the last N months.
    /// Keeps the top 6 parent categories by period total and aggregates any remainder into "Others".</summary>
    public async Task<ParentCategoryTrendResult> GetParentCategoryTrendAsync(
        Guid userId, int monthsBack, CancellationToken ct)
    {
        var todayBrt = DateTime.UtcNow.AddHours(-3).Date;
        var currentMonthStart = new DateTime(todayBrt.Year, todayBrt.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var rangeStartMonth = currentMonthStart.AddMonths(-(monthsBack - 1));
        var rangeEnd = new DateTime(
            currentMonthStart.Year, currentMonthStart.Month,
            DateTime.DaysInMonth(currentMonthStart.Year, currentMonthStart.Month), 23, 59, 59, DateTimeKind.Utc).AddDays(1);

        var rows = await dashboardQueries.GetMonthlyParentCategorySpendAsync(userId, rangeStartMonth.AddDays(-1), rangeEnd, ct);

        var months = new List<TrendMonth>(monthsBack);
        var monthIndexByYm = new Dictionary<(int Year, int Month), int>(monthsBack);
        for (var i = 0; i < monthsBack; i++)
        {
            var m = rangeStartMonth.AddMonths(i);
            months.Add(new TrendMonth(m.Year, m.Month));
            monthIndexByYm[(m.Year, m.Month)] = i;
        }

        // Group rows by parent category (null = Uncategorized)
        var totalsByCat = new Dictionary<string, (Guid? Id, string Name, string? Color, decimal[] Monthly)>();
        foreach (var row in rows)
        {
            if (!monthIndexByYm.TryGetValue((row.Year, row.Month), out var idx))
            {
                continue;
            }

            var key = row.CategoryId?.ToString() ?? "__uncategorized__";
            if (!totalsByCat.TryGetValue(key, out var entry))
            {
                entry = (
                    row.CategoryId,
                    row.CategoryId is null ? "Uncategorized" : (row.CategoryName ?? "Uncategorized"),
                    row.CategoryColor,
                    new decimal[monthsBack]);
                totalsByCat[key] = entry;
            }

            entry.Monthly[idx] += row.Amount;
        }

        var ranked = totalsByCat.Values
            .OrderByDescending(v => v.Monthly.Sum())
            .ToList();

        const int maxNamedSeries = 6;
        var top = ranked.Take(maxNamedSeries).ToList();
        var remainder = ranked.Skip(maxNamedSeries).ToList();

        var series = top
            .Select(c => new ParentCategoryTrendSeries(c.Id, c.Name, c.Color, c.Monthly))
            .ToList();

        if (remainder.Count > 0)
        {
            var othersMonthly = new decimal[monthsBack];
            foreach (var r in remainder)
            {
                for (var i = 0; i < monthsBack; i++)
                {
                    othersMonthly[i] += r.Monthly[i];
                }
            }

            if (othersMonthly.Sum() > 0m)
            {
                series.Add(new ParentCategoryTrendSeries(null, "Others", null, othersMonthly));
            }
        }

        return new ParentCategoryTrendResult(months, series);
    }

    // 0 when totalAmount is 0 -- never divide by zero.
    private static decimal CalculatePercent(decimal amount, decimal totalAmount) =>
        totalAmount == 0m ? 0m : amount / totalAmount * 100m;
}
