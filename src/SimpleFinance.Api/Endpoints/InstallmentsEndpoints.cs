using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.Installments;

namespace SimpleFinance.Api.Endpoints;

public static class InstallmentsEndpoints
{
    public static void MapInstallmentsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/installments").RequireAuthorization();
        group.MapGet("", GetInstallmentsAsync);
    }

    private static async Task<IResult> GetInstallmentsAsync(
        Guid? accountId,
        InstallmentsService installmentsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await installmentsService.GetActiveInstallmentsAsync(userId.Value, accountId, ct);
        return Results.Ok(result);
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
