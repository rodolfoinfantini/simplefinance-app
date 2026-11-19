using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Application.Categorization;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Categorization;

/// <summary>
/// Behavior tests for <see cref="AiCategorizationJob"/> (16-04 Task 2, AI-CAT-03/04/05). Runs on the
/// EF InMemory provider with a scripted <see cref="FakeAiCategorizationService"/>. Asserts the
/// hybrid-by-confidence split (auto-apply vs Pending suggestion), the never-overwrite rule
/// (Rule/UserOverride/AiSuggested/AiAuto rows are never classified nor mutated), idempotency (no
/// duplicate Pending suggestions), and the AI-off no-op.
/// </summary>
public class AiCategorizationJobTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AiCategorizationJob CreateJob(SimpleFinanceDbContext db, FakeAiCategorizationService classifier) =>
        new(db, classifier, NullLogger<AiCategorizationJob>.Instance);

    private static Guid SeedUser(
        SimpleFinanceDbContext db,
        bool aiEnabled = true,
        bool categorizationEnabled = true,
        double autoApplyThreshold = 0.85)
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Name = "Owner",
            Email = $"owner-{userId:N}@example.com",
            PasswordHash = "x",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AiEnabled = aiEnabled,
            AiCategorizationEnabled = categorizationEnabled,
            AiAutoApplyThreshold = autoApplyThreshold,
            AiChatModel = "deepseek-chat",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
        return userId;
    }

    private static Guid SeedCategory(SimpleFinanceDbContext db, Guid userId, string name = "Alimentação")
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        db.Categories.Add(new Category
        {
            Id = id,
            UserId = userId,
            Name = name,
            Type = CategoryType.Expense,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
        return id;
    }

    private static Transaction SeedTransaction(
        SimpleFinanceDbContext db,
        Guid userId,
        Guid? categoryId = null,
        CategorySource source = CategorySource.SystemMapping)
    {
        var now = DateTime.UtcNow;
        var txn = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = Guid.NewGuid(),
            PluggyTransactionId = $"ptx-{Guid.NewGuid():N}",
            Date = now,
            Description = "IFD*IFOOD",
            MerchantName = "iFood",
            Amount = -42.90m,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategoryId = categoryId,
            CategorySource = source,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Transactions.Add(txn);
        db.SaveChanges();
        return txn;
    }

    [Fact]
    public async Task Run_ConfidenceAboveThreshold_AutoAppliesAsAiAuto_NoSuggestion()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, autoApplyThreshold: 0.85);
        var categoryId = SeedCategory(db, userId);
        var txn = SeedTransaction(db, userId);

        var classifier = new FakeAiCategorizationService();
        classifier.Scripted[txn.Id] = new CategoryClassification(categoryId, 0.90, "text-embedding-3-small", ClassificationSource.Knn, null);

        await CreateJob(db, classifier).ExecuteAsync(CancellationToken.None);

        var fresh = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == txn.Id);
        Assert.Equal(categoryId, fresh.CategoryId);
        Assert.Equal(CategorySource.AiAuto, fresh.CategorySource);
        Assert.Equal(0.90, fresh.CategoryConfidence);
        Assert.Equal("text-embedding-3-small", fresh.CategoryModel);
        Assert.Empty(db.AiSuggestions);
    }

    [Fact]
    public async Task Run_ConfidenceBelowThreshold_CreatesPendingSuggestion_TransactionUnchanged()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, autoApplyThreshold: 0.85);
        var categoryId = SeedCategory(db, userId);
        var txn = SeedTransaction(db, userId);

        var classifier = new FakeAiCategorizationService();
        classifier.Scripted[txn.Id] = new CategoryClassification(categoryId, 0.50, "deepseek-chat", ClassificationSource.Llm, "iFood é comida");

        await CreateJob(db, classifier).ExecuteAsync(CancellationToken.None);

        var fresh = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == txn.Id);
        Assert.Null(fresh.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, fresh.CategorySource);
        Assert.Null(fresh.CategoryConfidence);
        Assert.Null(fresh.CategoryModel);

        var suggestion = Assert.Single(db.AiSuggestions);
        Assert.Equal(AiSuggestionType.Category, suggestion.Type);
        Assert.Equal(txn.Id, suggestion.TransactionId);
        Assert.Equal(AiSuggestionStatus.Pending, suggestion.Status);
        Assert.Equal(AiSuggestionSource.Llm, suggestion.Source);
        Assert.Equal(0.50, suggestion.Confidence);
        Assert.Equal("iFood é comida", suggestion.Rationale);
        Assert.Contains(categoryId.ToString(), suggestion.SuggestedPayload);
    }

    [Fact]
    public async Task Run_KnnSuggestion_MapsSourceToEmbedding()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, autoApplyThreshold: 0.99);
        var categoryId = SeedCategory(db, userId);
        var txn = SeedTransaction(db, userId);

        var classifier = new FakeAiCategorizationService();
        classifier.Scripted[txn.Id] = new CategoryClassification(categoryId, 0.80, "text-embedding-3-small", ClassificationSource.Knn, null);

        await CreateJob(db, classifier).ExecuteAsync(CancellationToken.None);

        var suggestion = Assert.Single(db.AiSuggestions);
        Assert.Equal(AiSuggestionSource.Embedding, suggestion.Source);
    }

    [Theory]
    [InlineData(CategorySource.Rule)]
    [InlineData(CategorySource.UserOverride)]
    [InlineData(CategorySource.AiSuggested)]
    [InlineData(CategorySource.AiAuto)]
    public async Task Run_NeverOverwritesProtectedSources_SkippedEntirely(CategorySource protectedSource)
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db);
        var categoryId = SeedCategory(db, userId);
        // Intentionally null CategoryId so ONLY the CategorySource guard protects it (proves the filter).
        var txn = SeedTransaction(db, userId, categoryId: null, source: protectedSource);

        var classifier = new FakeAiCategorizationService();
        // If the job ever classified it, it would auto-apply this high-confidence result.
        classifier.Scripted[txn.Id] = new CategoryClassification(categoryId, 0.99, "text-embedding-3-small", ClassificationSource.Knn, null);

        await CreateJob(db, classifier).ExecuteAsync(CancellationToken.None);

        Assert.DoesNotContain(txn.Id, classifier.ClassifiedTransactionIds);
        var fresh = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == txn.Id);
        Assert.Null(fresh.CategoryId);
        Assert.Equal(protectedSource, fresh.CategorySource);
        Assert.Empty(db.AiSuggestions);
    }

    [Fact]
    public async Task Run_RaisedThreshold_TurnsAutoApplyIntoSuggestion()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, autoApplyThreshold: 0.99);
        var categoryId = SeedCategory(db, userId);
        var txn = SeedTransaction(db, userId);

        var classifier = new FakeAiCategorizationService();
        classifier.Scripted[txn.Id] = new CategoryClassification(categoryId, 0.90, "text-embedding-3-small", ClassificationSource.Knn, null);

        await CreateJob(db, classifier).ExecuteAsync(CancellationToken.None);

        var fresh = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == txn.Id);
        Assert.Null(fresh.CategoryId);
        Assert.Single(db.AiSuggestions);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Run_AiDisabled_DoesNothing(bool aiEnabled, bool categorizationEnabled)
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: aiEnabled, categorizationEnabled: categorizationEnabled);
        var categoryId = SeedCategory(db, userId);
        var txn = SeedTransaction(db, userId);

        var classifier = new FakeAiCategorizationService();
        classifier.Scripted[txn.Id] = new CategoryClassification(categoryId, 0.99, "text-embedding-3-small", ClassificationSource.Knn, null);

        await CreateJob(db, classifier).ExecuteAsync(CancellationToken.None);

        Assert.Empty(classifier.ClassifiedTransactionIds);
        var fresh = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == txn.Id);
        Assert.Null(fresh.CategoryId);
        Assert.Empty(db.AiSuggestions);
    }

    [Fact]
    public async Task Run_ClassifierReturnsNone_NoWriteNoSuggestion()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db);
        SeedCategory(db, userId);
        var txn = SeedTransaction(db, userId);

        var classifier = new FakeAiCategorizationService();
        classifier.Scripted[txn.Id] = new CategoryClassification(null, 0.0, null, ClassificationSource.None, null);

        await CreateJob(db, classifier).ExecuteAsync(CancellationToken.None);

        var fresh = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == txn.Id);
        Assert.Null(fresh.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, fresh.CategorySource);
        Assert.Empty(db.AiSuggestions);
    }

    [Fact]
    public async Task Run_Idempotent_DoesNotDuplicatePendingSuggestion()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, autoApplyThreshold: 0.85);
        var categoryId = SeedCategory(db, userId);
        var txn = SeedTransaction(db, userId);

        var classifier = new FakeAiCategorizationService();
        classifier.Scripted[txn.Id] = new CategoryClassification(categoryId, 0.50, "deepseek-chat", ClassificationSource.Llm, "r1");

        await CreateJob(db, classifier).ExecuteAsync(CancellationToken.None);
        await CreateJob(db, classifier).ExecuteAsync(CancellationToken.None);

        Assert.Single(db.AiSuggestions);
        // The second run must not even re-classify the already-suggested transaction.
        Assert.Equal(1, classifier.ClassifiedTransactionIds.Count(id => id == txn.Id));
    }

    [Fact]
    public async Task Run_BatchedClassifier_Retries429WithBackoff_AndSavesAllTransactions()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, autoApplyThreshold: 0.85);
        var categoryId = SeedCategory(db, userId);
        var txn1 = SeedTransaction(db, userId);
        var txn2 = SeedTransaction(db, userId);

        var delays = new List<TimeSpan>();
        var batchClassifier = new FakeBatchAiCategorizationService(categoryId, rateLimitFailuresBeforeSuccess: 1);
        var job = new AiCategorizationJob(
            db,
            batchClassifier,
            NullLogger<AiCategorizationJob>.Instance,
            (span, _) =>
            {
                delays.Add(span);
                return Task.CompletedTask;
            });

        await job.ExecuteAsync(CancellationToken.None);

        Assert.Single(delays);
        Assert.Equal(2, batchClassifier.BatchCallCount); // 1 failed with 429 + 1 succeeded on retry
        var fresh1 = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == txn1.Id);
        var fresh2 = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == txn2.Id);
        Assert.Equal(categoryId, fresh1.CategoryId);
        Assert.Equal(categoryId, fresh2.CategoryId);
        Assert.Equal(CategorySource.AiAuto, fresh1.CategorySource);
        Assert.Equal(CategorySource.AiAuto, fresh2.CategorySource);
    }

    private sealed class FakeBatchAiCategorizationService(Guid categoryId, int rateLimitFailuresBeforeSuccess)
        : IBatchAiCategorizationService
    {
        private int _remaining429 = rateLimitFailuresBeforeSuccess;

        public int BatchCallCount { get; private set; }

        public Task<CategoryClassification> ClassifyAsync(Guid userId, Guid transactionId, CancellationToken ct) =>
            throw new NotSupportedException("Batched path should be invoked.");

        public Task<IReadOnlyDictionary<Guid, CategoryClassification>> ClassifyBatchAsync(
            Guid userId,
            IReadOnlyList<Guid> transactionIds,
            CancellationToken ct)
        {
            BatchCallCount++;
            if (_remaining429 > 0)
            {
                _remaining429--;
                throw new HttpRequestException("Too Many Requests", null, System.Net.HttpStatusCode.TooManyRequests);
            }

            IReadOnlyDictionary<Guid, CategoryClassification> map = transactionIds.ToDictionary(
                id => id,
                _ => new CategoryClassification(categoryId, 0.95, "models/gemini-3.5-flash-lite", ClassificationSource.Llm, "batch"));
            return Task.FromResult(map);
        }
    }

    /// <summary>
    /// Scripted <see cref="IAiCategorizationService"/> substitute: returns a canned
    /// <see cref="CategoryClassification"/> per transaction id (defaulting to None) and records every
    /// transaction id it was asked to classify, so tests can assert protected rows were never touched.
    /// </summary>
    private sealed class FakeAiCategorizationService : IAiCategorizationService
    {
        public Dictionary<Guid, CategoryClassification> Scripted { get; } = new();

        public List<Guid> ClassifiedTransactionIds { get; } = new();

        public Task<CategoryClassification> ClassifyAsync(Guid userId, Guid transactionId, CancellationToken ct)
        {
            ClassifiedTransactionIds.Add(transactionId);
            var result = Scripted.TryGetValue(transactionId, out var c)
                ? c
                : new CategoryClassification(null, 0.0, null, ClassificationSource.None, null);
            return Task.FromResult(result);
        }
    }
}
