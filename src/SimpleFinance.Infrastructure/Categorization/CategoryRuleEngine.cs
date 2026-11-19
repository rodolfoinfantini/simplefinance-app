using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Categorization;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Categorization;

/// <summary>
/// SYNC-06's single categorization call site, 04-04's real implementation (CAT-02/CAT-03):
/// evaluates the user's active CategoryRules in ascending Priority order (first match wins,
/// CategorySource.Rule); if none match, falls back to CategoryMapping (Pluggy category id first,
/// then a case-insensitive PluggyCategoryName match -- a resilience improvement over Phase 2's
/// id-only stub, since 04-02's seeded PluggyCategoryId guesses may be imprecise);
/// else returns an uncategorized result (null CategoryId, CategorySource.SystemMapping). Never
/// throws on missing rules/mappings/null category fields -- tolerates the same "empty table"
/// states MappingOnlyCategorizationService (the Phase 2 stub this replaces) tolerated.
///
/// Replaces MappingOnlyCategorizationService behind the exact same ICategorizationService seam
/// (ARCHITECTURE.md's phase-ordering-inversion guidance) -- SyncConnectionUseCase's call site is
/// unchanged in shape, only its input widened (04-04-PLAN.md).
/// </summary>
public sealed class CategoryRuleEngine : ICategorizationService
{
    private readonly SimpleFinanceDbContext _db;

    public CategoryRuleEngine(SimpleFinanceDbContext db)
    {
        _db = db;
    }

    public async Task<CategorizationResult> CategorizeAsync(Guid userId, CategorizationInput input, CancellationToken ct)
    {
        // T-04-10: always scoped to the caller's own user -- a rule belonging to another user
        // must never be evaluated, even if it would otherwise match.
        var rules = await _db.CategoryRules
            .Where(r => r.UserId == userId && r.IsActive)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        var matchInput = new RuleMatchInput(input.Description, input.MerchantName, input.Amount, input.PluggyCategory);
        var matched = RuleMatcher.SelectFirstMatch(rules, matchInput);

        if (matched is not null)
        {
            return new CategorizationResult(matched.CategoryId, CategorySource.Rule, matched.MarkAsTransfer);
        }

        var mappingCategoryId = await ResolveMappingCategoryIdAsync(input, ct);
        return new CategorizationResult(mappingCategoryId, CategorySource.SystemMapping);
    }

    private async Task<Guid?> ResolveMappingCategoryIdAsync(CategorizationInput input, CancellationToken ct)
    {
        if (input.PluggyCategoryId is not null)
        {
            var byId = await _db.CategoryMappings
                .FirstOrDefaultAsync(m => m.PluggyCategoryId == input.PluggyCategoryId, ct);

            if (byId is not null)
            {
                return byId.CategoryId;
            }
        }

        if (input.PluggyCategory is not null)
        {
            var byName = await _db.CategoryMappings
                .FirstOrDefaultAsync(m => m.PluggyCategoryName != null
                    && m.PluggyCategoryName.ToLower() == input.PluggyCategory.ToLower(), ct);

            if (byName is not null)
            {
                return byName.CategoryId;
            }
        }

        return null;
    }
}
