using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Application.Sync;

/// <summary>
/// SYNC-06's single categorization call site. Its Phase 2 implementation (02-04) was a
/// mapping-only stub over CategoryMapping; 04-04 replaces the implementation with the full
/// CategoryRuleEngine behind this exact interface, never changing the call site itself (per
/// ARCHITECTURE.md's explicit phase-ordering-inversion guidance) -- only its input widened.
/// </summary>
public interface ICategorizationService
{
    Task<CategorizationResult> CategorizeAsync(Guid userId, CategorizationInput input, CancellationToken ct);
}

/// <summary>
/// Carries everything CategoryRuleEngine needs to evaluate a transaction against both the user's
/// CategoryRules (DESCRIPTION/MERCHANT/AMOUNT/PLUGGY_CATEGORY matching, RF-CAT-003) and the
/// CategoryMapping fallback.
///
/// Widened beyond 04-04-PLAN.md's literal interfaces-block listing to carry BOTH
/// <see cref="PluggyCategoryId"/> (Pluggy's opaque category code, used for the primary
/// CategoryMapping id lookup) and <see cref="PluggyCategory"/> (the human-readable category name,
/// e.g. "Food and Drink"). The plan's own text requires both: RuleMatcher's PluggyCategory-field
/// rules must match against "the human-readable name field, not PluggyCategoryId... friendlier
/// for a user writing rules", and CategoryRuleEngine's CategoryMapping fallback needs the name
/// for its "id first, then case-insensitive PluggyCategoryName" resilience path. A single
/// PluggyCategoryId-only field (as literally listed) cannot satisfy either requirement.
/// </summary>
public sealed record CategorizationInput(string Description, string? MerchantName, decimal Amount, string? PluggyCategoryId, string? PluggyCategory);

public sealed record CategorizationResult(Guid? CategoryId, CategorySource Source, bool MarkAsTransfer = false);
