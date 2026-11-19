using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Categorization;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Categorization;

/// <summary>
/// Exercises CategoryRuleEngine (04-04 Task 2) -- the real ICategorizationService
/// implementation: active CategoryRules first (ascending Priority, first match wins,
/// CategorySource.Rule), else CategoryMapping fallback (id then name, CategorySource.SystemMapping),
/// else uncategorized. Against EF Core InMemory, matching this test namespace's established
/// pattern (SyncConnectionUseCaseTests).
/// </summary>
public class CategoryRuleEngineTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static CategoryRule SeedRule(
        SimpleFinanceDbContext db, Guid userId, Guid categoryId,
        RuleMatchField field, RuleMatchOperator op, string? matchValue,
        int priority, bool isActive = true, bool markAsTransfer = false)
    {
        var now = DateTime.UtcNow;
        var rule = new CategoryRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MatchField = field,
            MatchOperator = op,
            MatchValue = matchValue,
            CategoryId = categoryId,
            Priority = priority,
            IsActive = isActive,
            MarkAsTransfer = markAsTransfer,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CategoryRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    private static CategoryMapping SeedMapping(
        SimpleFinanceDbContext db, string pluggyCategoryId, string? pluggyCategoryName, Guid categoryId)
    {
        var now = DateTime.UtcNow;
        var mapping = new CategoryMapping
        {
            Id = Guid.NewGuid(),
            PluggyCategoryId = pluggyCategoryId,
            PluggyCategoryName = pluggyCategoryName,
            CategoryId = categoryId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CategoryMappings.Add(mapping);
        db.SaveChanges();
        return mapping;
    }

    private static CategorizationInput CreateInput(
        string description = "Padaria do Bairro",
        string? merchant = "Padaria do Bairro Ltda",
        decimal amount = -25.50m,
        string? pluggyCategoryId = "cat-food",
        string? pluggyCategory = "Food and Drink") =>
        new(description, merchant, amount, pluggyCategoryId, pluggyCategory);

    [Fact]
    public async Task CategorizeAsync_MatchingActiveRule_ReturnsRuleCategoryAndSourceRule()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var ruleCategoryId = Guid.NewGuid();
        var lowerPriorityCategoryId = Guid.NewGuid();

        // Higher priority number = evaluated LATER; the lower-number rule must win even though
        // both would match.
        SeedRule(db, userId, ruleCategoryId, RuleMatchField.Description, RuleMatchOperator.Contains, "padaria", priority: 1);
        SeedRule(db, userId, lowerPriorityCategoryId, RuleMatchField.Description, RuleMatchOperator.Contains, "padaria", priority: 2);

        var sut = new CategoryRuleEngine(db);
        var result = await sut.CategorizeAsync(userId, CreateInput(description: "Compra na Padaria"), CancellationToken.None);

        Assert.Equal(ruleCategoryId, result.CategoryId);
        Assert.Equal(CategorySource.Rule, result.Source);
    }

    [Fact]
    public async Task CategorizeAsync_InactiveRuleSkipped_FallsThroughToMappingFallback()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var mappingCategoryId = Guid.NewGuid();
        var inactiveRuleCategoryId = Guid.NewGuid();

        SeedRule(db, userId, inactiveRuleCategoryId, RuleMatchField.Description, RuleMatchOperator.Contains, "padaria", priority: 1, isActive: false);
        SeedMapping(db, "cat-food", "Food and Drink", mappingCategoryId);

        var sut = new CategoryRuleEngine(db);
        var result = await sut.CategorizeAsync(userId, CreateInput(description: "Compra na Padaria"), CancellationToken.None);

        Assert.Equal(mappingCategoryId, result.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, result.Source);
    }

    [Fact]
    public async Task CategorizeAsync_NoMatchingRule_FallsBackToMappingByPluggyCategoryId()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var mappingCategoryId = Guid.NewGuid();
        SeedMapping(db, "cat-food", "Food and Drink", mappingCategoryId);

        var sut = new CategoryRuleEngine(db);
        var result = await sut.CategorizeAsync(userId, CreateInput(pluggyCategoryId: "cat-food", pluggyCategory: "Something Else"), CancellationToken.None);

        Assert.Equal(mappingCategoryId, result.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, result.Source);
    }

    [Fact]
    public async Task CategorizeAsync_NoIdMatch_FallsBackToMappingByPluggyCategoryNameCaseInsensitive()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var mappingCategoryId = Guid.NewGuid();
        SeedMapping(db, "cat-different-id", "FOOD AND DRINK", mappingCategoryId);

        var sut = new CategoryRuleEngine(db);
        var result = await sut.CategorizeAsync(
            userId, CreateInput(pluggyCategoryId: "cat-food-unmapped", pluggyCategory: "food and drink"), CancellationToken.None);

        Assert.Equal(mappingCategoryId, result.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, result.Source);
    }

    [Fact]
    public async Task CategorizeAsync_NoRuleNoMapping_ReturnsNullCategoryAndSystemMappingSourceNeverThrows()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();

        var sut = new CategoryRuleEngine(db);
        var result = await sut.CategorizeAsync(
            userId, CreateInput(pluggyCategoryId: "unknown", pluggyCategory: "Unknown Category"), CancellationToken.None);

        Assert.Null(result.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, result.Source);
    }

    [Fact]
    public async Task CategorizeAsync_NullPluggyCategoryIdAndName_NoRules_ReturnsNullNeverThrows()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();

        var sut = new CategoryRuleEngine(db);
        var result = await sut.CategorizeAsync(
            userId, CreateInput(pluggyCategoryId: null, pluggyCategory: null), CancellationToken.None);

        Assert.Null(result.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, result.Source);
    }

    [Fact]
    public async Task CategorizeAsync_RulesScopedToOwnUser_NeverMatchesAnotherUsersRule()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var otherUsersCategoryId = Guid.NewGuid();

        // T-04-10: cross-user rule leakage must never happen -- a rule belonging to a different
        // user must never be evaluated even if it would otherwise match.
        SeedRule(db, otherUserId, otherUsersCategoryId, RuleMatchField.Description, RuleMatchOperator.Contains, "padaria", priority: 1);

        var sut = new CategoryRuleEngine(db);
        var result = await sut.CategorizeAsync(userId, CreateInput(description: "Compra na Padaria", pluggyCategoryId: "unknown", pluggyCategory: null), CancellationToken.None);

        Assert.Null(result.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, result.Source);
    }

    [Fact]
    public async Task CategorizeAsync_MatchingRuleWithMarkAsTransfer_PropagatesMarkAsTransferFlag()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var transferCategoryId = Guid.NewGuid();

        SeedRule(
            db, userId, transferCategoryId,
            RuleMatchField.Description, RuleMatchOperator.Contains, "Dinheiro retirado",
            priority: 1, markAsTransfer: true);

        var sut = new CategoryRuleEngine(db);
        var result = await sut.CategorizeAsync(userId, CreateInput(description: "Dinheiro retirado Fatura"), CancellationToken.None);

        Assert.Equal(transferCategoryId, result.CategoryId);
        Assert.Equal(CategorySource.Rule, result.Source);
        Assert.True(result.MarkAsTransfer);
    }
}
