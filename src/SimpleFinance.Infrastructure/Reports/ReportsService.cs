using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Recurring;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Reports;

/// <summary>RPT-01: one row per category-scoped Budget for (year, month), joined to that same
/// period's real spend via DashboardQueries.GetCategorySpendAsync -- never a second, divergent
/// spend formula. The CategoryId=null global cap is excluded from this report per
/// 05-CONTEXT.md's "optional global monthly cap" semantics (this report only lists
/// category-scoped budgets).</summary>
public sealed record BudgetRealizedRow(
    Guid CategoryId, string CategoryName, decimal PlannedAmount, decimal RealizedAmount, decimal Variance);

public sealed record WeekdaySpendItem(
    int DayOfWeek, decimal TotalAmount, int TransactionCount, decimal AverageAmount);

/// <summary>
/// RPT-01/RPT-03: assembles all 7 reports (spending-by-category, income-vs-expense, cash flow by
/// account, net-worth evolution, card statements by month, recurring/subscriptions, budget
/// realized-vs-planned), reusing DashboardQueries/ReportQueries/RecurringService wherever a
/// suitable aggregate already exists. Every method is a thin assembly/mapping layer with zero SQL
/// of its own beyond what those already-tested Dapper/EF classes provide -- ReportsEndpoints.cs
/// is a thin HTTP wrapper over this class only, no business logic duplicated there.
/// </summary>
public sealed class ReportsService(
    SimpleFinanceDbContext db,
    DashboardQueries dashboardQueries,
    ReportQueries reportQueries,
    RecurringService recurringService)
{
    /// <summary>Reuses DashboardQueries.GetCategorySpendAsync verbatim.</summary>
    public Task<IReadOnlyList<CategorySpend>> GetSpendingByCategoryAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct) =>
        dashboardQueries.GetCategorySpendAsync(userId, periodStart, periodEnd, ct);

    /// <summary>Reuses DashboardQueries.GetIncomeExpenseByMonthAsync verbatim.</summary>
    public Task<IReadOnlyList<MonthlyIncomeExpense>> GetIncomeExpenseAsync(
        Guid userId, DateTime rangeStart, DateTime rangeEnd, CancellationToken ct) =>
        dashboardQueries.GetIncomeExpenseByMonthAsync(userId, rangeStart, rangeEnd, ct);

    /// <summary>Mirrors NetWorthService.GetEvolutionAsync's exact range computation
    /// (rangeStart = UtcNow - days), delegating directly to the already-existing
    /// DashboardQueries.GetNetWorthEvolutionAsync -- never duplicated or reimplemented.</summary>
    public Task<IReadOnlyList<NetWorthPoint>> GetNetWorthEvolutionAsync(Guid userId, int days, CancellationToken ct)
    {
        var rangeStart = DateTime.UtcNow.AddDays(-days);
        return dashboardQueries.GetNetWorthEvolutionAsync(userId, rangeStart, ct);
    }

    /// <summary>Reuses ReportQueries.GetCashFlowByAccountAsync verbatim (RPT-03).</summary>
    public Task<IReadOnlyList<CashFlowByAccountRow>> GetCashFlowByAccountAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct) =>
        reportQueries.GetCashFlowByAccountAsync(userId, periodStart, periodEnd, ct);

    /// <summary>Reuses ReportQueries.GetCardStatementsByMonthAsync verbatim (RPT-03).</summary>
    public Task<IReadOnlyList<CardStatementRow>> GetCardStatementsAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct) =>
        reportQueries.GetCardStatementsByMonthAsync(userId, periodStart, periodEnd, ct);

    /// <summary>Reuses RecurringService.GetRecurringAsync verbatim -- never re-queries
    /// RecurringTransactions directly.</summary>
    public Task<RecurringListResult> GetRecurringReportAsync(Guid userId, CancellationToken ct) =>
        recurringService.GetRecurringAsync(userId, ct);

    /// <summary>
    /// Loads the user's category-scoped Budget rows for (year, month) (CategoryId != null,
    /// excluding the global cap), computes the period's [start, end] via the same inclusive-range
    /// convention BudgetService.GetPeriodRange uses, calls
    /// DashboardQueries.GetCategorySpendAsync once for that period, and joins each budget row to
    /// its matching spend row (0 when uncategorized-in-spend-but-budgeted).
    /// </summary>
    public async Task<IReadOnlyList<BudgetRealizedRow>> GetBudgetRealizedVsPlannedAsync(
        Guid userId, int year, int month, CancellationToken ct)
    {
        var budgets = await db.Budgets
            .Where(b => b.UserId == userId && b.Year == year && b.Month == month && b.CategoryId != null)
            .ToListAsync(ct);

        if (budgets.Count == 0)
        {
            return [];
        }

        var (periodStart, periodEnd) = GetPeriodRange(year, month);
        var spendRows = await dashboardQueries.GetCategorySpendAsync(userId, periodStart, periodEnd, ct);

        var categoryIds = budgets.Select(b => b.CategoryId!.Value).Distinct().ToList();
        var categoriesById = await db.Categories
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        return budgets.Select(b => ComputeRow(b, spendRows, categoriesById)).ToList();
    }

    private static readonly int[] MondayToSundayOrder = [1, 2, 3, 4, 5, 6, 0];

    /// <summary>Returns spending grouped by day of week ordered Monday (1) through Sunday (0),
    /// gap-filling any weekday with zero transactions.</summary>
    public async Task<IReadOnlyList<WeekdaySpendItem>> GetSpendingByWeekdayAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, bool excludeInstallments, CancellationToken ct)
    {
        var startUtc = periodStart.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(periodStart, DateTimeKind.Utc)
            : periodStart.ToUniversalTime();
        var endUtc = periodEnd.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(periodEnd, DateTimeKind.Utc)
            : periodEnd.ToUniversalTime();
        if (endUtc.TimeOfDay == TimeSpan.Zero)
        {
            endUtc = endUtc.Date.AddDays(1).AddTicks(-1);
        }

        var rows = await reportQueries.GetSpendingByWeekdayAsync(userId, startUtc, endUtc, excludeInstallments, ct);
        var byDow = rows.ToDictionary(r => r.DayOfWeek);

        return MondayToSundayOrder
            .Select(dow => byDow.TryGetValue(dow, out var row) && row.TransactionCount > 0
                ? new WeekdaySpendItem(
                    dow,
                    row.TotalAmount,
                    row.TransactionCount,
                    Math.Round(row.TotalAmount / row.TransactionCount, 2))
                : new WeekdaySpendItem(dow, 0m, 0, 0m))
            .ToList();
    }

    private static BudgetRealizedRow ComputeRow(
        Budget budget, IReadOnlyList<CategorySpend> spendRows, IReadOnlyDictionary<Guid, Category> categoriesById)
    {
        var categoryId = budget.CategoryId!.Value;
        var categoryName = categoriesById.TryGetValue(categoryId, out var category) ? category.Name : "Unknown";
        var realizedAmount = spendRows.FirstOrDefault(r => r.CategoryId == categoryId)?.Amount ?? 0m;
        var variance = budget.LimitAmount - realizedAmount;

        return new BudgetRealizedRow(categoryId, categoryName, budget.LimitAmount, realizedAmount, variance);
    }

    // Inclusive [first day 00:00:00 UTC, last day 23:59:59 UTC] range -- matches
    // DashboardQueries.cs's/BudgetService.GetPeriodRange's documented period-boundary convention
    // exactly, replicated here since ReportsService (Infrastructure) has no shared dependency on
    // BudgetService's private helper.
    private static (DateTime Start, DateTime End) GetPeriodRange(int year, int month)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59, DateTimeKind.Utc);
        return (start, end);
    }
}
