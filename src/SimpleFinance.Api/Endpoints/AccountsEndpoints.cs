using System.IdentityModel.Tokens.Jwt;
using System.Linq.Expressions;
using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Accounts.ToggleHide;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// ACCT-01..03: the read surface for every Account (bank + credit card) Phase 2's sync already
/// writes, joined with CreditCardDetail (1:1, populated for Type=Credit accounts only) and
/// Connection.InstitutionName, plus the ACCT-03 hide-from-totals mutation. Single-user app
/// (T-03-02 precedent) -- every row already belongs to the sole authenticated user, so GET has no
/// cross-tenant leakage risk. Mirrors ConnectionsEndpoints.cs's exact structure (group +
/// RequireAuthorization + GetUserId helper).
/// </summary>
public static class AccountsEndpoints
{
    public static void MapAccountsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").RequireAuthorization();
        group.MapGet("", GetAccountsAsync);
        group.MapPatch("/{id:guid}/hide", ToggleAccountHideAsync);
    }

    private static async Task<IResult> GetAccountsAsync(
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // No query params/pagination -- single-user account counts are small; return the full
        // set every call. A hidden account is still returned here (hide only affects
        // totals/dashboard elsewhere, per 03-UI-SPEC.md's copywriting contract).
        var accounts = await LoadAccountResponsesAsync(db, a => a.UserId == userId.Value, ct);
        return Results.Ok(accounts);
    }

    private static async Task<IResult> ToggleAccountHideAsync(
        Guid id,
        ToggleAccountHideCommand command,
        IValidator<ToggleAccountHideCommand> validator,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(command, ct);

        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // T-03-01: explicit UserId scoping in addition to the soft-delete query filter -- this is
        // a mutation, not a read, so defense-in-depth costs nothing.
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId.Value, ct);
        if (account is null)
        {
            return Results.NotFound();
        }

        account.IsHidden = command.IsHidden!.Value;
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var response = await LoadAccountResponsesAsync(db, a => a.Id == account.Id, ct);
        return Results.Ok(response.Single());
    }

    // Shared join+mapping helper so the list handler and the PATCH handler never duplicate the
    // CreditCardDetail/Connection join logic.
    private static async Task<List<AccountResponse>> LoadAccountResponsesAsync(
        SimpleFinanceDbContext db,
        Expression<Func<Account, bool>> predicate,
        CancellationToken ct)
    {
        var rows = await db.Accounts
            .Where(predicate)
            .GroupJoin(db.CreditCardDetails, a => a.Id, c => c.AccountId, (a, cards) => new { Account = a, Cards = cards })
            .SelectMany(x => x.Cards.DefaultIfEmpty(), (x, card) => new { x.Account, Card = card })
            .GroupJoin(db.Connections, x => x.Account.ConnectionId, conn => conn.Id, (x, conns) => new { x.Account, x.Card, Conns = conns })
            .SelectMany(x => x.Conns.DefaultIfEmpty(), (x, conn) => new { x.Account, x.Card, Connection = conn })
            .ToListAsync(ct);

        var creditCardsWithCloseDate = rows
            .Where(r => r.Account.Type == AccountType.Credit && r.Card?.BalanceCloseDate is not null)
            .Select(r => new { r.Account.Id, CloseDate = r.Card!.BalanceCloseDate!.Value })
            .ToList();

        var nextStatementByAccountId = new Dictionary<Guid, decimal?>();
        if (creditCardsWithCloseDate.Count > 0)
        {
            var cardIds = creditCardsWithCloseDate.Select(c => c.Id).ToList();
            var pendingTxns = await db.Transactions
                .Where(t => cardIds.Contains(t.AccountId)
                    && t.Status == TransactionStatus.Pending
                    && t.BillId == null)
                .Select(t => new { t.AccountId, t.Date, t.Amount, t.Type, t.Description })
                .ToListAsync(ct);

            foreach (var c in creditCardsWithCloseDate)
            {
                var closeCutoffUtc = DateTime.SpecifyKind(c.CloseDate.Date, DateTimeKind.Utc).AddHours(6);
                var nextCloseCutoffUtc = DateTime.SpecifyKind(c.CloseDate.Date.AddMonths(1), DateTimeKind.Utc).AddHours(6);

                var nextTotal = pendingTxns
                    .Where(t => t.AccountId == c.Id
                        && t.Date > closeCutoffUtc
                        && t.Date <= nextCloseCutoffUtc
                        && !(t.Type == TransactionType.Credit
                            && !string.IsNullOrWhiteSpace(t.Description)
                            && t.Description.Contains("pagamento", StringComparison.OrdinalIgnoreCase)))
                    .Sum(t => t.Type == TransactionType.Debit ? Math.Abs(t.Amount) : -Math.Abs(t.Amount));

                if (nextTotal > 0m)
                {
                    nextStatementByAccountId[c.Id] = nextTotal;
                }
            }
        }

        return rows
            .Select(r => ToResponse(
                r.Account,
                r.Card,
                !string.IsNullOrWhiteSpace(r.Connection?.CustomName)
                    ? r.Connection.CustomName
                    : r.Connection?.InstitutionName,
                nextStatementByAccountId.GetValueOrDefault(r.Account.Id)))
            .ToList();
    }

    private static AccountResponse ToResponse(
        Account a, CreditCardDetail? card, string? institutionName, decimal? nextStatementBalance = null) => new(
        a.Id,
        a.ConnectionId,
        institutionName,
        a.Type.ToString(),
        a.Subtype.ToString(),
        a.Name,
        a.MarketingName,
        a.NumberMasked,
        a.Balance,
        a.AutomaticallyInvestedBalance,
        a.CurrencyCode,
        a.IsHidden,
        card is null
            ? null
            : new CreditCardResponse(
                card.Brand,
                card.Level,
                card.BrandAdditionalInfo,
                card.CreditLimit,
                card.AvailableCredit,
                card.BalanceCloseDate,
                card.BalanceDueDate,
                card.MinimumPayment,
                card.LastStatementBalance ?? a.Balance,
                nextStatementBalance,
                card.Status.ToString(),
                card.HolderType));

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }

    private sealed record AccountResponse(
        Guid Id,
        Guid ConnectionId,
        string? InstitutionName,
        string Type,
        string Subtype,
        string Name,
        string? MarketingName,
        string? NumberMasked,
        decimal Balance,
        decimal? AutomaticallyInvestedBalance,
        string CurrencyCode,
        bool IsHidden,
        CreditCardResponse? CreditCard);

    private sealed record CreditCardResponse(
        string? Brand,
        string? Level,
        string? BrandAdditionalInfo,
        decimal? CreditLimit,
        decimal? AvailableCredit,
        DateTime? BalanceCloseDate,
        DateTime? BalanceDueDate,
        decimal? MinimumPayment,
        decimal? LastStatementBalance,
        decimal? NextStatementBalance,
        string Status,
        string? HolderType);
}
