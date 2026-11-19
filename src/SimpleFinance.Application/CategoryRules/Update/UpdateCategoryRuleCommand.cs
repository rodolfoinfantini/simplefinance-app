namespace SimpleFinance.Application.CategoryRules.Update;

/// <summary>
/// CAT-03: full field update of an existing CategoryRule -- same body shape as
/// CreateCategoryRuleCommand minus Priority. Priority changes go through the dedicated
/// PUT /api/category-rules/reorder route only, keeping "priority order" a single, predictable
/// code path (interfaces block, 04-05-PLAN.md).
/// </summary>
public sealed record UpdateCategoryRuleCommand(
    string MatchField,
    string MatchOperator,
    string? MatchValue,
    decimal? AmountMin,
    decimal? AmountMax,
    Guid CategoryId,
    bool IsActive,
    bool MarkAsTransfer = false);
