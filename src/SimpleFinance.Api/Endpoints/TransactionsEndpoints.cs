using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Transactions.ApplySimilar;
using SimpleFinance.Application.Transactions.UpdateTransaction;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// TXN-01..05: the unified, filtered, paginated transaction feed (GET /api/transactions, GET
/// /api/transactions/{id}) plus every mutation off the detail's kebab menu -- recategorize
/// (assign an existing Category only), note, hide, transfer toggle, and apply-to-similar bulk
/// assign. Mirrors ConnectionsEndpoints.cs/AccountsEndpoints.cs's exact structure (group +
/// RequireAuthorization + GetUserId helper). Single-user app -- every query is still explicitly
/// scoped by UserId (T-03-09) as defense-in-depth alongside the soft-delete query filter.
///
/// Every mutation that touches CategoryId sets CategorySource.UserOverride, and the transfer
/// toggle sets IsTransferManualOverride=true -- both are deliberate-user-action provenance
/// markers that protect the change from a future sync/03-02 transfer-detection re-run (PITFALLS.md
/// Pitfall 10).
/// </summary>
public static class TransactionsEndpoints
{
    private const int PageSize = 50;

    public static void MapTransactionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").RequireAuthorization();
        group.MapGet("", GetTransactionsAsync);
        group.MapGet("/{id:guid}", GetTransactionDetailAsync);
        group.MapPatch("/{id:guid}/category", RecategorizeTransactionAsync);
        group.MapPatch("/{id:guid}/note", UpdateTransactionNoteAsync);
        group.MapPatch("/{id:guid}/hide", ToggleTransactionHiddenAsync);
        group.MapPatch("/{id:guid}/transfer", ToggleTransactionTransferAsync);
        group.MapGet("/{id:guid}/similar", GetSimilarCountAsync);
        group.MapPost("/{id:guid}/apply-similar", ApplySimilarAsync);
    }

    private static async Task<IResult> GetTransactionsAsync(
        int? page,
        DateTime? dateFrom,
        DateTime? dateTo,
        Guid? accountId,
        Guid? categoryId,
        string? type,
        string? status,
        decimal? amountMin,
        decimal? amountMax,
        string? search,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var query = db.Transactions.Where(t => t.UserId == userId.Value);

        if (dateFrom is not null)
        {
            query = query.Where(t => t.Date >= dateFrom.Value);
        }

        if (dateTo is not null)
        {
            query = query.Where(t => t.Date <= dateTo.Value);
        }

        if (accountId is not null)
        {
            query = query.Where(t => t.AccountId == accountId.Value);
        }

        if (categoryId is not null)
        {
            var childCategoryIds = await db.Categories
                .Where(c => c.UserId == userId.Value && c.ParentCategoryId == categoryId.Value)
                .Select(c => (Guid?)c.Id)
                .ToListAsync(ct);

            query = childCategoryIds.Count > 0
                ? query.Where(t => t.CategoryId == categoryId.Value || childCategoryIds.Contains(t.CategoryId))
                : query.Where(t => t.CategoryId == categoryId.Value);
        }

        // Derived semantics (interfaces block, 03-03-PLAN.md): "income" = Credit && !IsTransfer,
        // "expense" = Debit && !IsTransfer, "transfer" = IsTransfer regardless of Type. A transfer
        // never counts as income or expense even if Type=Credit.
        if (type is not null)
        {
            query = type.ToLowerInvariant() switch
            {
                "income" => query.Where(t => t.Type == TransactionType.Credit && !t.IsTransfer),
                "expense" => query.Where(t => t.Type == TransactionType.Debit && !t.IsTransfer),
                "transfer" => query.Where(t => t.IsTransfer),
                _ => query,
            };
        }

        if (status is not null)
        {
            query = status.ToLowerInvariant() switch
            {
                "posted" => query.Where(t => t.Status == TransactionStatus.Posted),
                "pending" => query.Where(t => t.Status == TransactionStatus.Pending),
                _ => query,
            };
        }

        // T-03-08: amountMin/amountMax always compare Math.Abs(Amount) -- stays agnostic of
        // whichever sign convention Pluggy uses, never compares signed Amount against a
        // user-entered (always-positive) range.
        if (amountMin is not null)
        {
            query = query.Where(t => Math.Abs(t.Amount) >= amountMin.Value);
        }

        if (amountMax is not null)
        {
            query = query.Where(t => Math.Abs(t.Amount) <= amountMax.Value);
        }

        // T-03-07: EF Core parameterized LINQ .Contains() translation only -- never raw SQL
        // string concatenation. Case-insensitive substring match against Description OR
        // MerchantName.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.ToLowerInvariant();
            query = query.Where(t =>
                t.Description.ToLower().Contains(normalizedSearch)
                || (t.MerchantName != null && t.MerchantName.ToLower().Contains(normalizedSearch)));
        }

        var totalCount = await query.CountAsync(ct);

        var currentPage = page is null or < 1 ? 1 : page.Value;

        var pagedIds = await query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Skip((currentPage - 1) * PageSize)
            .Take(PageSize)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var items = await LoadTransactionListItemsAsync(db, pagedIds, ct);

        return Results.Ok(new { items, page = currentPage, pageSize = PageSize, totalCount });
    }

    private static async Task<IResult> GetTransactionDetailAsync(
        Guid id,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var transaction = await db.Transactions
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value, ct);
        if (transaction is null)
        {
            return Results.NotFound();
        }

        var detail = await BuildTransactionDetailResponseAsync(db, transaction, ct);
        return Results.Ok(detail);
    }

    private static async Task<IResult> RecategorizeTransactionAsync(
        Guid id,
        RecategorizeTransactionCommand command,
        IValidator<RecategorizeTransactionCommand> validator,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await ValidateWithUserAsync(validator, command, userId.Value, ct);

        var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value, ct);
        if (transaction is null)
        {
            return Results.NotFound();
        }

        // A null CategoryId still means the user deliberately chose "no category" -- still marks
        // UserOverride so a future rule engine (Phase 4) never silently re-touches this row.
        transaction.CategoryId = command.CategoryId;
        transaction.CategorySource = CategorySource.UserOverride;
        transaction.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var detail = await BuildTransactionDetailResponseAsync(db, transaction, ct);
        return Results.Ok(detail);
    }

    private static async Task<IResult> UpdateTransactionNoteAsync(
        Guid id,
        UpdateTransactionNoteCommand command,
        IValidator<UpdateTransactionNoteCommand> validator,
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

        var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value, ct);
        if (transaction is null)
        {
            return Results.NotFound();
        }

        transaction.Notes = string.IsNullOrEmpty(command.Note) ? null : command.Note;
        transaction.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var detail = await BuildTransactionDetailResponseAsync(db, transaction, ct);
        return Results.Ok(detail);
    }

    private static async Task<IResult> ToggleTransactionHiddenAsync(
        Guid id,
        ToggleTransactionHiddenCommand command,
        IValidator<ToggleTransactionHiddenCommand> validator,
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

        var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value, ct);
        if (transaction is null)
        {
            return Results.NotFound();
        }

        transaction.IsHidden = command.IsHidden!.Value;
        transaction.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var detail = await BuildTransactionDetailResponseAsync(db, transaction, ct);
        return Results.Ok(detail);
    }

    private static async Task<IResult> ToggleTransactionTransferAsync(
        Guid id,
        ToggleTransactionTransferCommand command,
        IValidator<ToggleTransactionTransferCommand> validator,
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

        var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value, ct);
        if (transaction is null)
        {
            return Results.NotFound();
        }

        // Setting IsTransferManualOverride=true in the same write is what makes 03-02's
        // TransferDetectionService skip this row on every future re-run, permanently.
        transaction.IsTransfer = command.IsTransfer!.Value;
        transaction.IsTransferManualOverride = true;
        transaction.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var detail = await BuildTransactionDetailResponseAsync(db, transaction, ct);
        return Results.Ok(detail);
    }

    private static async Task<IResult> GetSimilarCountAsync(
        Guid id,
        Guid categoryId,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var source = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value, ct);
        if (source is null)
        {
            return Results.NotFound();
        }

        var merchant = source.MerchantName ?? source.Description;
        var count = await BuildSimilarQuery(db, source, userId.Value, categoryId).CountAsync(ct);

        return Results.Ok(new { count, merchant });
    }

    private static async Task<IResult> ApplySimilarAsync(
        Guid id,
        ApplySimilarCommand command,
        IValidator<ApplySimilarCommand> validator,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await ValidateWithUserAsync(validator, command, userId.Value, ct);

        var source = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value, ct);
        if (source is null)
        {
            return Results.NotFound();
        }

        var matches = await BuildSimilarQuery(db, source, userId.Value, command.CategoryId).ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var match in matches)
        {
            match.CategoryId = command.CategoryId;
            match.CategorySource = CategorySource.UserOverride;
            match.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new { updatedCount = matches.Count });
    }

    // Shared "similar" match definition reused by both GetSimilarCountAsync (count only) and
    // ApplySimilarAsync (bulk update) -- merchant-or-description match key, same user, excluding
    // the source row itself, excluding rows that already carry the target categoryId.
    private static IQueryable<Transaction> BuildSimilarQuery(
        SimpleFinanceDbContext db, Transaction source, Guid userId, Guid categoryId)
    {
        var query = db.Transactions.Where(t =>
            t.UserId == userId
            && t.Id != source.Id
            && t.CategoryId != categoryId);

        return source.MerchantName is not null
            ? query.Where(t => t.MerchantName != null && t.MerchantName.ToLower() == source.MerchantName.ToLower())
            : query.Where(t => t.Description == source.Description);
    }

    // Threads the authenticated caller's UserId into FluentValidation's RootContextData so a
    // DB-dependent MustAsync rule (RecategorizeTransactionValidator/ApplySimilarValidator) can
    // scope its existence check to the caller without the command itself carrying UserId.
    private static async Task ValidateWithUserAsync<T>(IValidator<T> validator, T command, Guid userId, CancellationToken ct)
    {
        var context = new ValidationContext<T>(command);
        context.RootContextData["UserId"] = userId;
        var result = await validator.ValidateAsync(context, ct);
        if (!result.IsValid)
        {
            throw new ValidationException(result.Errors);
        }
    }

    // Shared join+mapping helper reused by both the list and detail handlers (per the plan's
    // action spec) so the Account/Category join logic is never duplicated.
    private static async Task<List<TransactionListItem>> LoadTransactionListItemsAsync(
        SimpleFinanceDbContext db, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Transactions
            .Where(t => ids.Contains(t.Id))
            .GroupJoin(db.Accounts, t => t.AccountId, a => a.Id, (t, accounts) => new { Transaction = t, Accounts = accounts })
            .SelectMany(x => x.Accounts.DefaultIfEmpty(), (x, account) => new { x.Transaction, Account = account })
            .GroupJoin(db.Categories, x => x.Transaction.CategoryId, c => c.Id, (x, categories) => new { x.Transaction, x.Account, Categories = categories })
            .SelectMany(x => x.Categories.DefaultIfEmpty(), (x, category) => new { x.Transaction, x.Account, Category = category })
            .ToListAsync(ct);

        // Skip/Take already applied to `ids` -- re-order the joined rows to match, since the
        // join query itself has no ORDER BY (EF Core doesn't guarantee join-preserved ordering).
        var byId = rows.ToDictionary(
            r => r.Transaction.Id,
            r => ToListItem(r.Transaction, r.Account?.Name, r.Category?.Name));

        return ids.Select(id => byId[id]).ToList();
    }

    private static async Task<(string? AccountName, string? CategoryName)> LoadNamesAsync(
        SimpleFinanceDbContext db, Guid accountId, Guid? categoryId, CancellationToken ct)
    {
        var accountName = await db.Accounts
            .Where(a => a.Id == accountId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(ct);

        string? categoryName = null;
        if (categoryId is not null)
        {
            categoryName = await db.Categories
                .Where(c => c.Id == categoryId.Value)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);
        }

        return (accountName, categoryName);
    }

    // Shared detail-mapper reused by the read (GetTransactionDetailAsync) and every mutation
    // handler above -- every PATCH route returns the same full detail shape after its write, per
    // the plan's own "reuse that exact private mapper method" instruction.
    private static async Task<TransactionDetailResponse> BuildTransactionDetailResponseAsync(
        SimpleFinanceDbContext db, Transaction transaction, CancellationToken ct)
    {
        var (accountName, categoryName) = await LoadNamesAsync(db, transaction.AccountId, transaction.CategoryId, ct);

        return new TransactionDetailResponse(
            transaction.Id,
            transaction.Date,
            transaction.Description,
            transaction.DescriptionRaw,
            transaction.MerchantName,
            transaction.MerchantDocument,
            transaction.AccountId,
            accountName,
            transaction.CategoryId,
            categoryName,
            transaction.Amount,
            transaction.Type.ToString(),
            transaction.Status.ToString(),
            transaction.IsTransfer,
            transaction.IsHidden,
            transaction.Notes,
            transaction.InstallmentNumber,
            transaction.TotalInstallments,
            transaction.BillId,
            transaction.CategorySource.ToString());
    }

    private static TransactionListItem ToListItem(Transaction t, string? accountName, string? categoryName) => new(
        t.Id,
        t.Date,
        t.Description,
        t.MerchantName,
        t.AccountId,
        accountName ?? string.Empty,
        t.CategoryId,
        categoryName,
        t.Amount,
        t.Type.ToString(),
        t.Status.ToString(),
        t.IsTransfer,
        t.IsHidden,
        t.CategorySource.ToString());

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }

    private sealed record TransactionListItem(
        Guid Id,
        DateTime Date,
        string Description,
        string? MerchantName,
        Guid AccountId,
        string AccountName,
        Guid? CategoryId,
        string? CategoryName,
        decimal Amount,
        string Type,
        string Status,
        bool IsTransfer,
        bool IsHidden,
        string CategorySource);

    private sealed record TransactionDetailResponse(
        Guid Id,
        DateTime Date,
        string Description,
        string? DescriptionRaw,
        string? MerchantName,
        string? MerchantDocument,
        Guid AccountId,
        string? AccountName,
        Guid? CategoryId,
        string? CategoryName,
        decimal Amount,
        string Type,
        string Status,
        bool IsTransfer,
        bool IsHidden,
        string? Notes,
        int? InstallmentNumber,
        int? TotalInstallments,
        string? BillId,
        string CategorySource);
}

/// <summary>
/// T-03-11: a non-null CategoryId must exist and belong to the caller. Null is always valid --
/// the user explicitly chose "no category" (still sets CategorySource.UserOverride in the
/// handler). Lives in the Api project (not Application, alongside RecategorizeTransactionCommand)
/// because it needs SimpleFinanceDbContext, and Application cannot reference Infrastructure
/// without a circular project reference (02-04-SUMMARY.md/03-02-SUMMARY.md precedent). The
/// caller's UserId is threaded in via ValidationContext.RootContextData
/// (TransactionsEndpoints.ValidateWithUserAsync), since the command itself only carries the
/// client-supplied CategoryId.
/// </summary>
public sealed class RecategorizeTransactionValidator : AbstractValidator<RecategorizeTransactionCommand>
{
    public RecategorizeTransactionValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.CategoryId)
            .MustAsync(async (_, categoryId, context, ct) =>
            {
                if (categoryId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Categories.AnyAsync(c => c.Id == categoryId.Value && c.UserId == userId, ct);
            })
            .WithMessage("categoryId does not exist or does not belong to the caller.");
    }
}

/// <summary>
/// T-03-11/T-03-13: same existence+ownership check as <see cref="RecategorizeTransactionValidator"/>,
/// same circular-reference rationale for living here instead of Application. CategoryId is
/// non-nullable on ApplySimilarCommand (apply-similar always assigns a real category, never
/// clears one), so no null short-circuit is needed here.
/// </summary>
public sealed class ApplySimilarValidator : AbstractValidator<ApplySimilarCommand>
{
    public ApplySimilarValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.CategoryId)
            .MustAsync(async (_, categoryId, context, ct) =>
            {
                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Categories.AnyAsync(c => c.Id == categoryId && c.UserId == userId, ct);
            })
            .WithMessage("categoryId does not exist or does not belong to the caller.");
    }
}
