using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.Dashboard;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// DASH-01..05: 6 independently-loadable GET /api/dashboard/* routes -- thin HTTP wrappers over
/// DashboardService (05-06), which owns every bit of actual aggregation/ranking/gap-fill/join
/// logic. Mirrors BudgetsEndpoints.cs/TransactionsEndpoints.cs's exact structure (group +
/// RequireAuthorization + GetUserId helper). No business logic lives in this file -- every
/// handler is 3-5 lines that resolve DashboardService from DI, call the matching method, and
/// Results.Ok(...) the response.
///
/// T-05-09: dateFrom/dateTo are required (400 if either is missing or unparsable -- ASP.NET
/// Core's own minimal-API binder already 400s an UNPARSABLE DateTime? query value; the explicit
/// null-check below additionally covers the MISSING case, which the binder alone leaves as a
/// silent null rather than a 400) and months is restricted to exactly {6, 12}.
/// </summary>
public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").RequireAuthorization();
        group.MapGet("/summary", GetSummaryAsync);
        group.MapGet("/spending-by-category", GetSpendingByCategoryAsync);
        group.MapGet("/spending-by-parent-category", GetSpendingByParentCategoryAsync);
        group.MapGet("/spending-pace", GetSpendingPaceAsync);
        group.MapGet("/parent-category-trend", GetParentCategoryTrendAsync);
        group.MapGet("/income-expense", GetIncomeExpenseAsync);
        group.MapGet("/net-worth-evolution", GetNetWorthEvolutionAsync);
        group.MapGet("/at-risk-budgets", GetAtRiskBudgetsAsync);
        group.MapGet("/upcoming-due-dates", GetUpcomingDueDatesAsync);
    }

    private static async Task<IResult> GetSummaryAsync(
        DateTime? dateFrom,
        DateTime? dateTo,
        DashboardService dashboardService,
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

        var result = await dashboardService.GetSummaryAsync(userId.Value, dateFrom.Value, dateTo.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetSpendingByCategoryAsync(
        DateTime? dateFrom,
        DateTime? dateTo,
        DashboardService dashboardService,
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

        var result = await dashboardService.GetSpendingByCategoryAsync(userId.Value, dateFrom.Value, dateTo.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetSpendingByParentCategoryAsync(
        DateTime? dateFrom,
        DateTime? dateTo,
        DashboardService dashboardService,
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

        var result = await dashboardService.GetSpendingByParentCategoryAsync(userId.Value, dateFrom.Value, dateTo.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetSpendingPaceAsync(
        DashboardService dashboardService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await dashboardService.GetSpendingPaceAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetParentCategoryTrendAsync(
        int? months,
        DashboardService dashboardService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var resolvedMonths = months ?? 6;
        if (resolvedMonths != 6 && resolvedMonths != 12)
        {
            return Results.BadRequest(new { error = "months must be exactly 6 or 12." });
        }

        var result = await dashboardService.GetParentCategoryTrendAsync(userId.Value, resolvedMonths, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetIncomeExpenseAsync(
        int? months,
        DashboardService dashboardService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var resolvedMonths = months ?? 6;
        if (resolvedMonths != 6 && resolvedMonths != 12)
        {
            return Results.BadRequest(new { error = "months must be exactly 6 or 12." });
        }

        var result = await dashboardService.GetIncomeExpenseAsync(userId.Value, monthsBack: resolvedMonths, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetNetWorthEvolutionAsync(
        int? months,
        DashboardService dashboardService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var resolvedMonths = months ?? 6;
        if (resolvedMonths != 6 && resolvedMonths != 12)
        {
            return Results.BadRequest(new { error = "months must be exactly 6 or 12." });
        }

        var result = await dashboardService.GetNetWorthEvolutionAsync(userId.Value, resolvedMonths, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetAtRiskBudgetsAsync(
        DashboardService dashboardService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await dashboardService.GetAtRiskBudgetsAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetUpcomingDueDatesAsync(
        DashboardService dashboardService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await dashboardService.GetUpcomingDueDatesAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
