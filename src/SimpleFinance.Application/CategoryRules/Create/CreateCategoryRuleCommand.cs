namespace SimpleFinance.Application.CategoryRules.Create;

/// <summary>
/// CAT-03: creates a new user-owned CategoryRule. <see cref="MatchField"/>/<see cref="MatchOperator"/>
/// are strings parsed against RuleMatchField/RuleMatchOperator by CreateCategoryRuleValidator (Api
/// project, see CategoryRulesEndpoints.cs) so an invalid value returns 400 instead of a raw
/// model-binding failure -- mirrors 04-03's CreateCategoryCommand.Type pattern. <see cref="Priority"/>
/// is optional: when omitted, the handler defaults it to (max existing priority for this user) + 1
/// so a newly-created rule never accidentally jumps to the front of evaluation order.
/// </summary>
public sealed record CreateCategoryRuleCommand(
    string MatchField,
    string MatchOperator,
    string? MatchValue,
    decimal? AmountMin,
    decimal? AmountMax,
    Guid CategoryId,
    int? Priority,
    bool IsActive,
    bool MarkAsTransfer = false);
