using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.Export;
using SimpleFinance.Infrastructure.Reports;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// RPT-02: GET /api/export/transactions and GET /api/export/reports/{reportType}, both
/// format=csv|xlsx -- thin HTTP wrappers over ExportService (Infrastructure), which owns every
/// bit of table-building/writing logic. Mirrors ReportsEndpoints.cs's exact structure (group +
/// RequireAuthorization + GetUserId helper).
///
/// T-07-13: reportType is validated against a fixed 7-slug allow-list before any dispatch -- an
/// unknown value 400s rather than reaching ExportService.BuildReportTableAsync's switch default.
/// T-07-14: filenames are built from a fixed server-side pattern ({type}-{yyyyMMdd}.{ext}) --
/// never derived from user-controlled input.
/// </summary>
public static class ExportEndpoints
{
    private static readonly string[] AllowedReportTypes =
    [
        "spending-by-category",
        "income-expense",
        "net-worth-evolution",
        "cash-flow-by-account",
        "card-statements",
        "recurring",
        "budget-realized-vs-planned",
    ];

    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/export").RequireAuthorization();
        group.MapGet("/transactions", ExportTransactionsAsync);
        group.MapGet("/reports/{reportType}", ExportReportAsync);
    }

    private static async Task<IResult> ExportTransactionsAsync(
        string? format,
        DateTime? dateFrom,
        DateTime? dateTo,
        Guid? accountId,
        Guid? categoryId,
        string? type,
        string? status,
        decimal? amountMin,
        decimal? amountMax,
        string? search,
        bool? excludeTransfers,
        ExportService exportService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!TryParseFormat(format, out var resolvedFormat))
        {
            return Results.BadRequest(new { error = "format must be csv or xlsx." });
        }

        var filters = new TransactionExportFilters(
            dateFrom, dateTo, accountId, categoryId, type, status, amountMin, amountMax, search, excludeTransfers);
        var table = await exportService.BuildTransactionsTableAsync(userId.Value, filters, ct);

        var fileName = $"transactions-{DateTime.UtcNow:yyyyMMdd}";
        return WriteExportResult(exportService, table, resolvedFormat, fileName);
    }

    private static async Task<IResult> ExportReportAsync(
        string reportType,
        string? format,
        DateTime? dateFrom,
        DateTime? dateTo,
        int? days,
        int? year,
        int? month,
        ExportService exportService,
        ReportsService reportsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!AllowedReportTypes.Contains(reportType))
        {
            return Results.BadRequest(new { error = "Unknown report type." });
        }

        if (!TryParseFormat(format, out var resolvedFormat))
        {
            return Results.BadRequest(new { error = "format must be csv or xlsx." });
        }

        var query = new ReportExportQuery(dateFrom, dateTo, days, year, month);

        ExportTable table;
        try
        {
            table = await exportService.BuildReportTableAsync(reportType, reportsService, userId.Value, query, ct);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var fileName = $"{reportType}-{DateTime.UtcNow:yyyyMMdd}";
        return WriteExportResult(exportService, table, resolvedFormat, fileName);
    }

    private static IResult WriteExportResult(ExportService exportService, ExportTable table, string format, string fileNameBase)
    {
        using var stream = new MemoryStream();

        if (format == "csv")
        {
            exportService.WriteCsv(table, stream);
            return Results.File(stream.ToArray(), "text/csv", $"{fileNameBase}.csv");
        }

        exportService.WriteXlsx(table, stream);
        return Results.File(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{fileNameBase}.xlsx");
    }

    private static bool TryParseFormat(string? format, out string resolvedFormat)
    {
        switch (format?.ToLowerInvariant())
        {
            case "csv":
                resolvedFormat = "csv";
                return true;
            case "xlsx":
                resolvedFormat = "xlsx";
                return true;
            default:
                resolvedFormat = string.Empty;
                return false;
        }
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
