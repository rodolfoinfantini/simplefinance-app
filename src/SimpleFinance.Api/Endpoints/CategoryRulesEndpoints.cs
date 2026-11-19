using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.CategoryRules;
using SimpleFinance.Application.CategoryRules.Create;
using SimpleFinance.Application.CategoryRules.Reorder;
using SimpleFinance.Application.CategoryRules.TestRule;
using SimpleFinance.Application.CategoryRules.ToggleActive;
using SimpleFinance.Application.CategoryRules.Update;
using SimpleFinance.Application.Categorization;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// CAT-03/CAT-04: full CategoryRule CRUD (create/edit/reorder/toggle-active/delete), a "test rule"
/// preview endpoint (count + sample matches against real transactions, no writes), and a retroactive
/// "apply now" bulk-recategorization endpoint. Both test-rule and apply-now reuse RuleMatcher.IsMatch
/// (04-04) directly -- never a second matching implementation. Mirrors CategoriesEndpoints.cs's exact
/// structure (group + RequireAuthorization + GetUserId helper + T-04-10 defense-in-depth UserId
/// scoping on every write).
/// </summary>
public static class CategoryRulesEndpoints
{
    public static void MapCategoryRulesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/category-rules").RequireAuthorization();
        group.MapGet("", GetCategoryRulesAsync);
        group.MapPost("", CreateCategoryRuleAsync);
        group.MapPost("/test", TestCategoryRuleAsync);
        group.MapPut("/reorder", ReorderCategoryRulesAsync);
        group.MapPut("/{id:guid}", UpdateCategoryRuleAsync);
        group.MapPatch("/{id:guid}/active", ToggleCategoryRuleActiveAsync);
        group.MapPost("/{id:guid}/apply-now", ApplyRuleNowAsync);
        group.MapDelete("/{id:guid}", DeleteCategoryRuleAsync);
    }

    private static async Task<IResult> GetCategoryRulesAsync(
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var rules = await LoadCategoryRuleResponsesAsync(db, userId.Value, ct);
        return Results.Ok(rules);
    }

    private static async Task<IResult> CreateCategoryRuleAsync(
        CreateCategoryRuleCommand command,
        IValidator<CreateCategoryRuleCommand> validator,
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

        int priority;
        if (command.Priority is not null)
        {
            priority = command.Priority.Value;
        }
        else
        {
            // Defaults to (max existing priority for this user) + 1 so a newly-created rule
            // never accidentally jumps to the front of evaluation order (interfaces block).
            var maxPriority = await db.CategoryRules
                .Where(r => r.UserId == userId.Value)
                .Select(r => (int?)r.Priority)
                .MaxAsync(ct);
            priority = (maxPriority ?? -1) + 1;
        }

        var now = DateTime.UtcNow;
        var rule = new CategoryRule
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            MatchField = Enum.Parse<RuleMatchField>(command.MatchField, ignoreCase: true),
            MatchOperator = Enum.Parse<RuleMatchOperator>(command.MatchOperator, ignoreCase: true),
            MatchValue = command.MatchValue,
            AmountMin = command.AmountMin,
            AmountMax = command.AmountMax,
            CategoryId = command.CategoryId,
            Priority = priority,
            IsActive = command.IsActive,
            MarkAsTransfer = command.MarkAsTransfer,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CategoryRules.Add(rule);
        await db.SaveChangesAsync(ct);

        var categoryName = await db.Categories
            .Where(c => c.Id == rule.CategoryId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

        return Results.Created($"/api/category-rules/{rule.Id}", ToResponse(rule, categoryName));
    }

    private static async Task<IResult> UpdateCategoryRuleAsync(
        Guid id,
        UpdateCategoryRuleCommand command,
        IValidator<UpdateCategoryRuleCommand> validator,
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

        var rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId.Value, ct);
        if (rule is null)
        {
            return Results.NotFound();
        }

        // Priority is intentionally untouched here -- priority changes go through the dedicated
        // reorder route only (interfaces block).
        rule.MatchField = Enum.Parse<RuleMatchField>(command.MatchField, ignoreCase: true);
        rule.MatchOperator = Enum.Parse<RuleMatchOperator>(command.MatchOperator, ignoreCase: true);
        rule.MatchValue = command.MatchValue;
        rule.AmountMin = command.AmountMin;
        rule.AmountMax = command.AmountMax;
        rule.CategoryId = command.CategoryId;
        rule.IsActive = command.IsActive;
        rule.MarkAsTransfer = command.MarkAsTransfer;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var categoryName = await db.Categories
            .Where(c => c.Id == rule.CategoryId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(ToResponse(rule, categoryName));
    }

    private static async Task<IResult> ToggleCategoryRuleActiveAsync(
        Guid id,
        ToggleCategoryRuleActiveCommand command,
        IValidator<ToggleCategoryRuleActiveCommand> validator,
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

        var rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId.Value, ct);
        if (rule is null)
        {
            return Results.NotFound();
        }

        rule.IsActive = command.IsActive!.Value;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var categoryName = await db.Categories
            .Where(c => c.Id == rule.CategoryId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(ToResponse(rule, categoryName));
    }

    private static async Task<IResult> ReorderCategoryRulesAsync(
        ReorderCategoryRulesCommand command,
        IValidator<ReorderCategoryRulesCommand> validator,
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

        var rules = await db.CategoryRules.Where(r => r.UserId == userId.Value).ToListAsync(ct);
        var rulesById = rules.ToDictionary(r => r.Id);

        var now = DateTime.UtcNow;
        for (var index = 0; index < command.OrderedIds.Length; index++)
        {
            var rule = rulesById[command.OrderedIds[index]];
            rule.Priority = index;
            rule.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        var updated = await LoadCategoryRuleResponsesAsync(db, userId.Value, ct);
        return Results.Ok(updated);
    }

    private static async Task<IResult> DeleteCategoryRuleAsync(
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

        var rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId.Value, ct);
        if (rule is null)
        {
            return Results.NotFound();
        }

        // Rules are never "system" -- no delete-protection needed (unlike Category).
        db.CategoryRules.Remove(rule);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> TestCategoryRuleAsync(
        TestCategoryRuleCommand command,
        IValidator<TestCategoryRuleCommand> validator,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await validator.ValidateAndThrowAsync(command, ct);

        // Transient, non-persisted CategoryRule-shaped struct built purely to reuse
        // RuleMatcher.IsMatch -- never added to db.CategoryRules, no SaveChangesAsync call
        // anywhere in this handler (T-04-13: read-only preview).
        var draftRule = new CategoryRule
        {
            MatchField = Enum.Parse<RuleMatchField>(command.MatchField, ignoreCase: true),
            MatchOperator = Enum.Parse<RuleMatchOperator>(command.MatchOperator, ignoreCase: true),
            MatchValue = command.MatchValue,
            AmountMin = command.AmountMin,
            AmountMax = command.AmountMax,
        };

        var transactions = await db.Transactions
            .Where(t => t.UserId == userId.Value)
            .OrderByDescending(t => t.Date)
            .Select(t => new
            {
                t.Id,
                t.Date,
                t.Description,
                t.MerchantName,
                t.Amount,
                t.PluggyCategory,
            })
            .ToListAsync(ct);

        // RuleMatcher reuse (T-04-SC) -- same matching definition test-rule and apply-now and
        // automatic sync-time categorization all share, so preview results can never drift from
        // what a saved/active rule would actually do.
        var matches = transactions
            .Where(t => RuleMatcher.IsMatch(draftRule, new RuleMatchInput(t.Description, t.MerchantName, t.Amount, t.PluggyCategory)))
            .ToList();

        // Already most-recent-first from the OrderByDescending(t => t.Date) above; Where()
        // preserves source ordering.
        var sample = matches
            .Take(20)
            .Select(t => new { t.Id, t.Date, t.Description, t.MerchantName, t.Amount })
            .ToList();

        return Results.Ok(new { count = matches.Count, sample });
    }

    private static async Task<IResult> ApplyRuleNowAsync(
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

        var rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId.Value, ct);
        if (rule is null)
        {
            return Results.NotFound();
        }

        // T-04-12: runs regardless of rule.IsActive (explicit one-time action per 04-01's
        // interfaces block) but ALWAYS excludes CategorySource.UserOverride -- the load-bearing
        // mitigation for this entire plan's CAT-04 delivery. Never touches a manually-overridden
        // transaction, independent of whether the rule would otherwise match it.
        var candidates = await db.Transactions
            .Where(t => t.UserId == userId.Value && t.CategorySource != CategorySource.UserOverride)
            .ToListAsync(ct);

        // Inline synchronous processing (not a Hangfire background job) per 04-CONTEXT.md's
        // explicit discretion -- single-user realistic transaction volumes (PITFALLS.md's
        // "2-5 years of history" scale note) stay well within a request timeout (T-04-13,
        // accepted risk; revisit only if real usage proves otherwise).
        var now = DateTime.UtcNow;
        var updatedCount = 0;
        foreach (var transaction in candidates)
        {
            var input = new RuleMatchInput(transaction.Description, transaction.MerchantName, transaction.Amount, transaction.PluggyCategory);
            if (!RuleMatcher.IsMatch(rule, input))
            {
                continue;
            }

            transaction.CategoryId = rule.CategoryId;
            transaction.CategorySource = CategorySource.Rule;
            if (rule.MarkAsTransfer && !transaction.IsTransferManualOverride)
            {
                transaction.IsTransfer = true;
            }
            transaction.UpdatedAt = now;
            updatedCount++;
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new { updatedCount });
    }

    // Reused by GetCategoryRulesAsync and ReorderCategoryRulesAsync so the list-shape projection
    // (including the Category name join) is never duplicated.
    private static async Task<List<CategoryRuleResponse>> LoadCategoryRuleResponsesAsync(
        SimpleFinanceDbContext db, Guid userId, CancellationToken ct)
    {
        return await db.CategoryRules
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.Priority)
            .GroupJoin(db.Categories, r => r.CategoryId, c => c.Id, (r, categories) => new { Rule = r, Categories = categories })
            .SelectMany(x => x.Categories.DefaultIfEmpty(), (x, category) => new CategoryRuleResponse(
                x.Rule.Id,
                x.Rule.MatchField.ToString(),
                x.Rule.MatchOperator.ToString(),
                x.Rule.MatchValue,
                x.Rule.AmountMin,
                x.Rule.AmountMax,
                x.Rule.CategoryId,
                category != null ? category.Name : null,
                x.Rule.Priority,
                x.Rule.IsActive,
                x.Rule.MarkAsTransfer))
            .ToListAsync(ct);
    }

    private static CategoryRuleResponse ToResponse(CategoryRule rule, string? categoryName) => new(
        rule.Id,
        rule.MatchField.ToString(),
        rule.MatchOperator.ToString(),
        rule.MatchValue,
        rule.AmountMin,
        rule.AmountMax,
        rule.CategoryId,
        categoryName,
        rule.Priority,
        rule.IsActive,
        rule.MarkAsTransfer);

    // Threads the authenticated caller's UserId into FluentValidation's RootContextData so a
    // DB-dependent MustAsync rule (CreateCategoryRuleValidator/UpdateCategoryRuleValidator/
    // ReorderCategoryRulesValidator) can scope its check without the command itself carrying
    // UserId. Mirrors CategoriesEndpoints.ValidateWithUserAsync<T> exactly (04-03 precedent).
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

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }

    private sealed record CategoryRuleResponse(
        Guid Id,
        string MatchField,
        string MatchOperator,
        string? MatchValue,
        decimal? AmountMin,
        decimal? AmountMax,
        Guid CategoryId,
        string? CategoryName,
        int Priority,
        bool IsActive,
        bool MarkAsTransfer);
}

/// <summary>
/// T-04-11: categoryId must exist and belong to the caller. Field/operator/value combination
/// rules (RANGE requires both bounds, Amount-only-Equals/Range, non-Amount-never-Range, REGEX
/// length cap + pre-compile check) are shared with UpdateCategoryRuleValidator/
/// TestCategoryRuleValidator via CategoryRuleValidationRules. Lives in the Api project (not
/// Application) because it needs SimpleFinanceDbContext for the categoryId ownership MustAsync
/// check, and Application cannot reference Infrastructure without a circular project reference
/// (02-04/03-05/04-03 precedent). The caller's UserId is threaded in via
/// ValidationContext.RootContextData (CategoryRulesEndpoints.ValidateWithUserAsync).
/// </summary>
public sealed class CreateCategoryRuleValidator : AbstractValidator<CreateCategoryRuleCommand>
{
    public CreateCategoryRuleValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.MatchField)
            .Must(field => Enum.TryParse<RuleMatchField>(field, ignoreCase: true, out _))
            .WithMessage("matchField must be one of: Description, Merchant, PluggyCategory, Amount.");

        RuleFor(x => x.MatchOperator)
            .Must(op => Enum.TryParse<RuleMatchOperator>(op, ignoreCase: true, out _))
            .WithMessage("matchOperator must be one of: Contains, Equals, Regex, Range.");

        RuleFor(x => x)
            .Must(cmd => !CategoryRuleValidationRules.TryParseFields(cmd.MatchField, cmd.MatchOperator, out var field, out var op)
                || CategoryRuleValidationRules.HasValidFieldOperatorCombination(field, op, cmd.MatchValue, cmd.AmountMin, cmd.AmountMax))
            .WithMessage("Invalid matchField/matchOperator combination, or a required matchValue/amountMin/amountMax is missing.");

        RuleFor(x => x.MatchValue)
            .Must(v => !CategoryRuleValidationRules.ExceedsRegexMaxLength(v))
            .When(IsRegex)
            .WithMessage("matchValue (regex pattern) must not exceed 200 characters.");

        RuleFor(x => x.MatchValue)
            .Must(CategoryRuleValidationRules.IsValidRegexPattern)
            .When(IsRegex)
            .WithMessage("matchValue is not a valid regular expression.");

        RuleFor(x => x.CategoryId)
            .MustAsync(async (_, categoryId, context, ct) =>
            {
                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Categories.AnyAsync(c => c.Id == categoryId && c.UserId == userId, ct);
            })
            .WithMessage("categoryId does not exist or does not belong to the caller.");
    }

    private static bool IsRegex(CreateCategoryRuleCommand cmd) =>
        Enum.TryParse<RuleMatchOperator>(cmd.MatchOperator, ignoreCase: true, out var op)
        && CategoryRuleValidationRules.IsRegexOperator(op);
}

/// <summary>
/// Same rules as <see cref="CreateCategoryRuleValidator"/>, same circular-reference rationale for
/// living here instead of Application.
/// </summary>
public sealed class UpdateCategoryRuleValidator : AbstractValidator<UpdateCategoryRuleCommand>
{
    public UpdateCategoryRuleValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.MatchField)
            .Must(field => Enum.TryParse<RuleMatchField>(field, ignoreCase: true, out _))
            .WithMessage("matchField must be one of: Description, Merchant, PluggyCategory, Amount.");

        RuleFor(x => x.MatchOperator)
            .Must(op => Enum.TryParse<RuleMatchOperator>(op, ignoreCase: true, out _))
            .WithMessage("matchOperator must be one of: Contains, Equals, Regex, Range.");

        RuleFor(x => x)
            .Must(cmd => !CategoryRuleValidationRules.TryParseFields(cmd.MatchField, cmd.MatchOperator, out var field, out var op)
                || CategoryRuleValidationRules.HasValidFieldOperatorCombination(field, op, cmd.MatchValue, cmd.AmountMin, cmd.AmountMax))
            .WithMessage("Invalid matchField/matchOperator combination, or a required matchValue/amountMin/amountMax is missing.");

        RuleFor(x => x.MatchValue)
            .Must(v => !CategoryRuleValidationRules.ExceedsRegexMaxLength(v))
            .When(IsRegex)
            .WithMessage("matchValue (regex pattern) must not exceed 200 characters.");

        RuleFor(x => x.MatchValue)
            .Must(CategoryRuleValidationRules.IsValidRegexPattern)
            .When(IsRegex)
            .WithMessage("matchValue is not a valid regular expression.");

        RuleFor(x => x.CategoryId)
            .MustAsync(async (_, categoryId, context, ct) =>
            {
                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Categories.AnyAsync(c => c.Id == categoryId && c.UserId == userId, ct);
            })
            .WithMessage("categoryId does not exist or does not belong to the caller.");
    }

    private static bool IsRegex(UpdateCategoryRuleCommand cmd) =>
        Enum.TryParse<RuleMatchOperator>(cmd.MatchOperator, ignoreCase: true, out var op)
        && CategoryRuleValidationRules.IsRegexOperator(op);
}

/// <summary>
/// The array's id-set must exactly match the caller's current CategoryRule id-set -- a
/// partial/stale reorder would otherwise silently drop a rule out of the priority sequence. Lives
/// in the Api project for the same circular-reference (SimpleFinanceDbContext) rationale as the
/// Create/Update validators above.
/// </summary>
public sealed class ReorderCategoryRulesValidator : AbstractValidator<ReorderCategoryRulesCommand>
{
    public ReorderCategoryRulesValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.OrderedIds)
            .MustAsync(async (_, orderedIds, context, ct) =>
            {
                var userId = (Guid)context.RootContextData["UserId"]!;
                var existingIds = await db.CategoryRules
                    .Where(r => r.UserId == userId)
                    .Select(r => r.Id)
                    .ToListAsync(ct);

                return existingIds.Count == orderedIds.Length && existingIds.ToHashSet().SetEquals(orderedIds);
            })
            .WithMessage("orderedIds must contain every one of the caller's existing rule ids exactly once.");
    }
}
