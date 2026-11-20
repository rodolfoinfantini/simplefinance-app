using System.Globalization;
using ClosedXML.Excel;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reports;

namespace SimpleFinance.Infrastructure.Export;

/// <summary>
/// The same 7 filter dimensions TransactionsEndpoints.GetTransactionsAsync exposes (03-03), minus
/// pagination -- "export what I'm currently looking at" is a faithful mirror of the on-screen
/// feed's filter semantics, never a subtly different result set.
/// </summary>
public sealed record TransactionExportFilters(
    DateTime? DateFrom,
    DateTime? DateTo,
    Guid? AccountId,
    Guid? CategoryId,
    string? Type,
    string? Status,
    decimal? AmountMin,
    decimal? AmountMax,
    string? Search,
    bool? ExcludeTransfers = null);

/// <summary>
/// Optional query parameters a report export may need, depending on which of the 7 report types
/// is requested -- dateFrom/dateTo (period-based reports), days (net-worth-evolution), year/month
/// (budget-realized-vs-planned). All optional; <see cref="ExportService.BuildReportTableAsync"/>
/// defaults each report's missing parameters sensibly (current month / 30 days).
/// </summary>
public sealed record ReportExportQuery(
    DateTime? DateFrom, DateTime? DateTo, int? Days, int? Year, int? Month, bool? ExcludeInstallments = null);

/// <summary>
/// RPT-02: generic CSV (CsvHelper)/XLSX (ClosedXML) writers over the report-agnostic <see
/// cref="ExportTable"/> shape, plus the 2 table-builders (transactions, reports) that map
/// domain/report rows into that shape. No report-specific writer logic exists anywhere in this
/// class -- WriteCsv/WriteXlsx are the only 2 places bytes are actually produced, and both are
/// completely report-agnostic.
///
/// BuildReportTableAsync never issues its own SQL/LINQ query -- every branch calls the exact same
/// ReportsService (07-05) method the on-screen Reports page reads from, so an export can never
/// diverge from what the user already sees.
/// </summary>
public sealed class ExportService(SimpleFinanceDbContext db)
{
    /// <summary>
    /// Builds the transactions export table, headers: Date, Description, Merchant, Account,
    /// Category, Type, Amount, Status, Transfer, Hidden. Applies the same 7 filter dimensions
    /// TransactionsEndpoints.GetTransactionsAsync uses, unpaginated, ordered Date descending.
    ///
    /// Two additional baseline rules apply on top of those filters (07-CONTEXT.md's "exports
    /// respect the same transfer/hidden exclusions as on-screen figures" + must_haves' "no
    /// double-counted or leaked-transfer rows in an export"): hidden transactions (IsHidden=true,
    /// "Hide from reports" per the transaction feed's own copy) are always excluded -- an export
    /// is report-like output, not the raw ledger. Internal transfers (IsTransfer=true) are
    /// excluded by default too, UNLESS the caller explicitly asks for them via type=transfer (the
    /// same escape hatch TransactionsEndpoints itself already exposes) -- a plain "export
    /// everything" run never leaks a double-counted internal transfer into a statement meant to be
    /// taken external.
    /// </summary>
    public async Task<ExportTable> BuildTransactionsTableAsync(
        Guid userId, TransactionExportFilters filters, CancellationToken ct)
    {
        var query = db.Transactions.Where(t => t.UserId == userId);

        if (filters.DateFrom is not null)
        {
            var fromUtc = NormalizeUtc(filters.DateFrom.Value);
            query = query.Where(t => t.Date >= fromUtc);
        }

        if (filters.DateTo is not null)
        {
            var toUtc = NormalizeDateToEndOfDayUtc(filters.DateTo.Value);
            query = query.Where(t => t.Date <= toUtc);
        }

        if (filters.AccountId is not null)
        {
            query = query.Where(t => t.AccountId == filters.AccountId.Value);
        }

        if (filters.CategoryId is not null)
        {
            var childCategoryIds = await db.Categories
                .Where(c => c.UserId == userId && c.ParentCategoryId == filters.CategoryId.Value)
                .Select(c => (Guid?)c.Id)
                .ToListAsync(ct);

            query = childCategoryIds.Count > 0
                ? query.Where(t => t.CategoryId == filters.CategoryId.Value || childCategoryIds.Contains(t.CategoryId))
                : query.Where(t => t.CategoryId == filters.CategoryId.Value);
        }

        if (filters.Type is not null)
        {
            query = filters.Type.ToLowerInvariant() switch
            {
                "income" => query.Where(t => t.Type == TransactionType.Credit && !t.IsTransfer),
                "expense" => query.Where(t => t.Type == TransactionType.Debit && !t.IsTransfer),
                "transfer" => query.Where(t => t.IsTransfer),
                _ => query.Where(t => !t.IsTransfer),
            };
        }
        else
        {
            query = query.Where(t => !t.IsTransfer);
        }

        if (filters.ExcludeTransfers == true)
        {
            query = query.Where(t => !t.IsTransfer);
        }

        // Hidden transactions never appear in an export, regardless of any other filter.
        query = query.Where(t => !t.IsHidden);

        if (filters.Status is not null)
        {
            query = filters.Status.ToLowerInvariant() switch
            {
                "posted" => query.Where(t => t.Status == TransactionStatus.Posted),
                "pending" => query.Where(t => t.Status == TransactionStatus.Pending),
                _ => query,
            };
        }

        // T-03-08's amountMin/amountMax convention, reused verbatim: always compares
        // Math.Abs(Amount), never signed Amount against a user-entered always-positive range.
        if (filters.AmountMin is not null)
        {
            query = query.Where(t => Math.Abs(t.Amount) >= filters.AmountMin.Value);
        }

        if (filters.AmountMax is not null)
        {
            query = query.Where(t => Math.Abs(t.Amount) <= filters.AmountMax.Value);
        }

        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var normalizedSearch = filters.Search.ToLowerInvariant();
            query = query.Where(t =>
                t.Description.ToLower().Contains(normalizedSearch)
                || (t.MerchantName != null && t.MerchantName.ToLower().Contains(normalizedSearch)));
        }

        var joined = await query
            .GroupJoin(db.Accounts, t => t.AccountId, a => a.Id, (t, accounts) => new { Transaction = t, Accounts = accounts })
            .SelectMany(x => x.Accounts.DefaultIfEmpty(), (x, account) => new { x.Transaction, Account = account })
            .GroupJoin(db.Categories, x => x.Transaction.CategoryId, c => c.Id, (x, categories) => new { x.Transaction, x.Account, Categories = categories })
            .SelectMany(x => x.Categories.DefaultIfEmpty(), (x, category) => new { x.Transaction, x.Account, Category = category })
            .ToListAsync(ct);

        var rows = joined
            .OrderByDescending(x => x.Transaction.Date)
            .Select(x => new[]
            {
                FormatDate(x.Transaction.Date),
                x.Transaction.Description,
                x.Transaction.MerchantName ?? string.Empty,
                x.Account?.Name ?? string.Empty,
                x.Category?.Name ?? string.Empty,
                x.Transaction.Type.ToString(),
                FormatAmount(x.Transaction.Amount),
                x.Transaction.Status.ToString(),
                x.Transaction.IsTransfer.ToString(),
                x.Transaction.IsHidden.ToString(),
            })
            .ToList();

        return new ExportTable(
            ["Date", "Description", "Merchant", "Account", "Category", "Type", "Amount", "Status", "Transfer", "Hidden"],
            rows);
    }

    /// <summary>
    /// Single switch over the 7 known reportType slugs -- each branch calls the matching
    /// ReportsService method (07-05) and maps its result rows into string[] rows with
    /// report-appropriate headers. An unknown slug throws ArgumentException, mapped by
    /// ExportEndpoints to 400.
    /// </summary>
    public async Task<ExportTable> BuildReportTableAsync(
        string reportType, ReportsService reportsService, Guid userId, ReportExportQuery query, CancellationToken ct)
    {
        return reportType switch
        {
            "spending-by-category" => await BuildSpendingByCategoryTableAsync(reportsService, userId, query, ct),
            "income-expense" => await BuildIncomeExpenseTableAsync(reportsService, userId, query, ct),
            "net-worth-evolution" => await BuildNetWorthEvolutionTableAsync(reportsService, userId, query, ct),
            "cash-flow-by-account" => await BuildCashFlowByAccountTableAsync(reportsService, userId, query, ct),
            "card-statements" => await BuildCardStatementsTableAsync(reportsService, userId, query, ct),
            "recurring" => await BuildRecurringTableAsync(reportsService, userId, ct),
            "budget-realized-vs-planned" => await BuildBudgetRealizedTableAsync(reportsService, userId, query, ct),
            "spending-by-weekday" => await BuildSpendingByWeekdayTableAsync(reportsService, userId, query, ct),
            _ => throw new ArgumentException($"Unknown report type: {reportType}", nameof(reportType)),
        };
    }

    /// <summary>
    /// Writes <paramref name="table"/> as CSV via CsvHelper.CsvWriter, CultureInfo.InvariantCulture
    /// (BRL decimal separators must never leak into a CSV parsed with commas as the field
    /// delimiter) -- header row then every data row, WriteField/NextRecord. Leaves
    /// <paramref name="output"/> open for the caller (ExportEndpoints owns the MemoryStream's
    /// lifetime/bytes).
    /// </summary>
    public void WriteCsv(ExportTable table, Stream output)
    {
        using var writer = new StreamWriter(output, leaveOpen: true);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture, leaveOpen: true);

        foreach (var header in table.Headers)
        {
            csv.WriteField(header);
        }

        csv.NextRecord();

        foreach (var row in table.Rows)
        {
            foreach (var cell in row)
            {
                csv.WriteField(cell);
            }

            csv.NextRecord();
        }
    }

    /// <summary>
    /// Writes <paramref name="table"/> as an .xlsx workbook via ClosedXML.Excel.XLWorkbook: one
    /// worksheet named "Export", table.Headers in row 1 (Bold = true), every table.Rows entry
    /// starting at row 2 as plain string cells (ExportTable's cells are already
    /// formatted strings -- no type coercion needed), columns auto-sized before saving.
    /// </summary>
    public void WriteXlsx(ExportTable table, Stream output)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Export");

        for (var col = 0; col < table.Headers.Length; col++)
        {
            var headerCell = worksheet.Cell(1, col + 1);
            headerCell.Value = table.Headers[col];
            headerCell.Style.Font.Bold = true;
        }

        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            for (var col = 0; col < row.Length; col++)
            {
                worksheet.Cell(rowIndex + 2, col + 1).Value = row[col];
            }
        }

        worksheet.Columns().AdjustToContents();
        workbook.SaveAs(output);
    }

    private static async Task<ExportTable> BuildSpendingByCategoryTableAsync(
        ReportsService reportsService, Guid userId, ReportExportQuery query, CancellationToken ct)
    {
        var (start, end) = ResolvePeriod(query.DateFrom, query.DateTo);
        var rows = await reportsService.GetSpendingByCategoryAsync(userId, start, end, ct);

        var tableRows = rows
            .Select(r => new[] { r.CategoryName ?? "Uncategorized", FormatAmount(r.Amount) })
            .ToList();

        return new ExportTable(["Category", "Amount"], tableRows);
    }

    private static async Task<ExportTable> BuildIncomeExpenseTableAsync(
        ReportsService reportsService, Guid userId, ReportExportQuery query, CancellationToken ct)
    {
        var (start, end) = ResolvePeriod(query.DateFrom, query.DateTo);
        var rows = await reportsService.GetIncomeExpenseAsync(userId, start, end, ct);

        var tableRows = rows
            .Select(r => new[]
            {
                r.Year.ToString(CultureInfo.InvariantCulture),
                r.Month.ToString(CultureInfo.InvariantCulture),
                FormatAmount(r.Income),
                FormatAmount(r.Expense),
            })
            .ToList();

        return new ExportTable(["Year", "Month", "Income", "Expense"], tableRows);
    }

    private static async Task<ExportTable> BuildNetWorthEvolutionTableAsync(
        ReportsService reportsService, Guid userId, ReportExportQuery query, CancellationToken ct)
    {
        var days = query.Days ?? 30;
        var rows = await reportsService.GetNetWorthEvolutionAsync(userId, days, ct);

        var tableRows = rows
            .Select(r => new[] { FormatDate(r.Date), FormatAmount(r.NetWorth) })
            .ToList();

        return new ExportTable(["Date", "NetWorth"], tableRows);
    }

    private static async Task<ExportTable> BuildCashFlowByAccountTableAsync(
        ReportsService reportsService, Guid userId, ReportExportQuery query, CancellationToken ct)
    {
        var (start, end) = ResolvePeriod(query.DateFrom, query.DateTo);
        var rows = await reportsService.GetCashFlowByAccountAsync(userId, start, end, ct);

        var tableRows = rows
            .Select(r => new[] { r.AccountName, FormatAmount(r.Inflow), FormatAmount(r.Outflow), FormatAmount(r.Net) })
            .ToList();

        return new ExportTable(["Account", "Inflow", "Outflow", "Net"], tableRows);
    }

    private static async Task<ExportTable> BuildCardStatementsTableAsync(
        ReportsService reportsService, Guid userId, ReportExportQuery query, CancellationToken ct)
    {
        var (start, end) = ResolvePeriod(query.DateFrom, query.DateTo);
        var rows = await reportsService.GetCardStatementsAsync(userId, start, end, ct);

        var tableRows = rows
            .Select(r => new[]
            {
                r.AccountName,
                r.Year.ToString(CultureInfo.InvariantCulture),
                r.Month.ToString(CultureInfo.InvariantCulture),
                FormatAmount(r.StatementTotal),
                r.CreditLimit is not null ? FormatAmount(r.CreditLimit.Value) : string.Empty,
                r.BalanceDueDate is not null ? FormatDate(r.BalanceDueDate.Value) : string.Empty,
            })
            .ToList();

        return new ExportTable(["Account", "Year", "Month", "StatementTotal", "CreditLimit", "BalanceDueDate"], tableRows);
    }

    private static async Task<ExportTable> BuildRecurringTableAsync(
        ReportsService reportsService, Guid userId, CancellationToken ct)
    {
        var result = await reportsService.GetRecurringReportAsync(userId, ct);

        var tableRows = result.Items
            .Select(r => new[]
            {
                r.DisplayName,
                r.CategoryName ?? "Uncategorized",
                FormatAmount(r.AverageAmount),
                r.Frequency,
                FormatDate(r.LastSeenDate),
                FormatDate(r.NextExpectedDate),
                FormatAmount(r.YearlyTotal),
                r.IsSubscription.ToString(),
                r.IsActive.ToString(),
            })
            .ToList();

        return new ExportTable(
            ["DisplayName", "Category", "AverageAmount", "Frequency", "LastSeenDate", "NextExpectedDate", "YearlyTotal", "IsSubscription", "IsActive"],
            tableRows);
    }

    private static async Task<ExportTable> BuildBudgetRealizedTableAsync(
        ReportsService reportsService, Guid userId, ReportExportQuery query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var year = query.Year ?? now.Year;
        var month = query.Month ?? now.Month;
        var rows = await reportsService.GetBudgetRealizedVsPlannedAsync(userId, year, month, ct);

        var tableRows = rows
            .Select(r => new[] { r.CategoryName, FormatAmount(r.PlannedAmount), FormatAmount(r.RealizedAmount), FormatAmount(r.Variance) })
            .ToList();

        return new ExportTable(["Category", "Planned", "Realized", "Variance"], tableRows);
    }

    private static async Task<ExportTable> BuildSpendingByWeekdayTableAsync(
        ReportsService reportsService, Guid userId, ReportExportQuery query, CancellationToken ct)
    {
        var (start, end) = ResolvePeriod(query.DateFrom, query.DateTo);
        var rows = await reportsService.GetSpendingByWeekdayAsync(
            userId, start, end, query.ExcludeInstallments ?? true, ct);

        var dayNames = new Dictionary<int, string>
        {
            [0] = "Sunday",
            [1] = "Monday",
            [2] = "Tuesday",
            [3] = "Wednesday",
            [4] = "Thursday",
            [5] = "Friday",
            [6] = "Saturday",
        };

        var tableRows = rows
            .Select(r => new[]
            {
                dayNames.GetValueOrDefault(r.DayOfWeek, r.DayOfWeek.ToString(CultureInfo.InvariantCulture)),
                FormatAmount(r.TotalAmount),
                r.TransactionCount.ToString(CultureInfo.InvariantCulture),
                FormatAmount(r.AverageAmount),
            })
            .ToList();

        return new ExportTable(["DayOfWeek", "TotalAmount", "TransactionCount", "AverageAmount"], tableRows);
    }

    // Defaults to the current calendar month (inclusive [first day 00:00:00 UTC, last day
    // 23:59:59 UTC]) when dateFrom/dateTo aren't both supplied -- matches
    // ReportsService.GetPeriodRange's exact boundary convention (07-05-SUMMARY.md).
    private static (DateTime Start, DateTime End) ResolvePeriod(DateTime? dateFrom, DateTime? dateTo)
    {
        if (dateFrom is not null && dateTo is not null)
        {
            return (dateFrom.Value, dateTo.Value);
        }

        var now = DateTime.UtcNow;
        var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 59, 59, DateTimeKind.Utc);
        return (start, end);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static DateTime NormalizeDateToEndOfDayUtc(DateTime value)
    {
        var utc = NormalizeUtc(value);
        return utc.TimeOfDay == TimeSpan.Zero ? utc.Date.AddDays(1).AddTicks(-1) : utc;
    }

    private static string FormatAmount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
