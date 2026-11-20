using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.Reports;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// RPT-01: 7 GET /api/reports/* routes -- thin HTTP wrappers over ReportsService (07-05), which
/// owns every bit of actual aggregation/assembly logic. Mirrors DashboardEndpoints.cs's exact
/// 6-thin-handlers-per-file structure (group + RequireAuthorization + GetUserId helper). No
/// business logic lives in this file -- every handler resolves ReportsService from DI, maps the
/// result, and 400s on a missing required query param.
///
/// T-07-12: net-worth-evolution's `days` is restricted to exactly {30, 90, 365} (mirrors
/// DashboardEndpoints.cs's `months` restriction / NetWorthEndpoints.cs's `days` restriction);
/// year/month are bound to `int` by ASP.NET Core's own minimal-API model binder, which 400s a
/// non-numeric value before it ever reaches the query.
/// </summary>
public static class ReportsEndpoints
{
    public static void MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").RequireAuthorization();
        group.MapGet("/spending-by-category", GetSpendingByCategoryAsync);
        group.MapGet("/income-expense", GetIncomeExpenseAsync);
        group.MapGet("/net-worth-evolution", GetNetWorthEvolutionAsync);
        group.MapGet("/cash-flow-by-account", GetCashFlowByAccountAsync);
        group.MapGet("/card-statements", GetCardStatementsAsync);
        group.MapGet("/recurring", GetRecurringAsync);
        group.MapGet("/budget-realized-vs-planned", GetBudgetRealizedVsPlannedAsync);
        group.MapGet("/spending-by-weekday", GetSpendingByWeekdayAsync);
    }

    private static async Task<IResult> GetSpendingByCategoryAsync(
        DateTime? dateFrom,
        DateTime? dateTo,
        ReportsService reportsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (dateFrom is null || dateTo is null)
        {
            return Results.BadRequest(new { error = "dateFrom and dateTo are required." });
        }

        var result = await reportsService.GetSpendingByCategoryAsync(userId.Value, dateFrom.Value, dateTo.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetIncomeExpenseAsync(
        DateTime? dateFrom,
        DateTime? dateTo,
        ReportsService reportsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (dateFrom is null || dateTo is null)
        {
            return Results.BadRequest(new { error = "dateFrom and dateTo are required." });
        }

        var result = await reportsService.GetIncomeExpenseAsync(userId.Value, dateFrom.Value, dateTo.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetNetWorthEvolutionAsync(
        int? days,
        ReportsService reportsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var resolvedDays = days ?? 30;
        if (resolvedDays != 30 && resolvedDays != 90 && resolvedDays != 365)
        {
            return Results.BadRequest(new { error = "days must be exactly 30, 90, or 365." });
        }

        var result = await reportsService.GetNetWorthEvolutionAsync(userId.Value, resolvedDays, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetCashFlowByAccountAsync(
        DateTime? dateFrom,
        DateTime? dateTo,
        ReportsService reportsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (dateFrom is null || dateTo is null)
        {
            return Results.BadRequest(new { error = "dateFrom and dateTo are required." });
        }

        var result = await reportsService.GetCashFlowByAccountAsync(userId.Value, dateFrom.Value, dateTo.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetCardStatementsAsync(
        DateTime? dateFrom,
        DateTime? dateTo,
        ReportsService reportsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (dateFrom is null || dateTo is null)
        {
            return Results.BadRequest(new { error = "dateFrom and dateTo are required." });
        }

        var result = await reportsService.GetCardStatementsAsync(userId.Value, dateFrom.Value, dateTo.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetRecurringAsync(
        ReportsService reportsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await reportsService.GetRecurringReportAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetBudgetRealizedVsPlannedAsync(
        int? year,
        int? month,
        ReportsService reportsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (year is null || month is null)
        {
            return Results.BadRequest(new { error = "year and month are required." });
        }

        var result = await reportsService.GetBudgetRealizedVsPlannedAsync(userId.Value, year.Value, month.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetSpendingByWeekdayAsync(
        DateTime? dateFrom,
        DateTime? dateTo,
        bool? excludeInstallments,
        ReportsService reportsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (dateFrom is null || dateTo is null)
        {
            return Results.BadRequest(new { error = "dateFrom and dateTo are required." });
        }

        var result = await reportsService.GetSpendingByWeekdayAsync(
            userId.Value, dateFrom.Value, dateTo.Value, excludeInstallments ?? true, ct);
        return Results.Ok(result);
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
