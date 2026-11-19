using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pgvector;
using SimpleFinance.Application.Categorization;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Categorization;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.UnitTests.Ai.Fakes;

namespace SimpleFinance.UnitTests.Categorization;

/// <summary>
/// Per-cascade-level tests for <see cref="AiCategorizationService"/> (16-03 Task 2, AI-CAT-02/03).
/// Runs on the EF InMemory provider with fakes: the real deterministic <see cref="CategoryRuleEngine"/>
/// (levels 1-2), a <see cref="FakeKnnCategoryVoter"/> (level 3 — the real voter needs the pgvector
/// column InMemory cannot map), <see cref="FakeEmbeddingClient"/> for the on-demand query vector, and
/// <see cref="FakeLlmClient"/> (level 4). Every test asserts the cascade's stop-at-first-hit ordering,
/// its graceful degradation, and that it WRITES NOTHING (the transaction is never mutated).
/// </summary>
public class AiCategorizationServiceTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IOptions<AiOptions> Options() => Microsoft.Extensions.Options.Options.Create(new AiOptions
    {
        OpenAiEmbeddingModel = "text-embedding-3-small",
    });

    private static AiCategorizationService CreateService(
        SimpleFinanceDbContext db,
        FakeKnnCategoryVoter voter,
        FakeLlmClient llm,
        FakeEmbeddingClient embedding,
        IAiKeyProvider? keyProvider = null) =>
        new(
            db,
            new CategoryRuleEngine(db),
            voter,
            embedding,
            llm,
            new PiiRedactor(),
            Options(),
            NullLogger<AiCategorizationService>.Instance,
            keyProvider);

    private static Guid SeedUser(SimpleFinanceDbContext db, bool aiEnabled, bool categorizationEnabled, string chatModel = "deepseek-chat")
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
            AiChatModel = chatModel,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
        return userId;
    }

    private static Guid SeedCategory(SimpleFinanceDbContext db, Guid userId, string name)
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
        string description,
        string? merchant,
        string? pluggyCategoryId = null,
        string? pluggyCategory = null)
    {
        var now = DateTime.UtcNow;
        var txn = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = Guid.NewGuid(),
            PluggyTransactionId = $"ptx-{Guid.NewGuid():N}",
            Date = now,
            Description = description,
            MerchantName = merchant,
            Amount = -42.90m,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategorySource = CategorySource.SystemMapping,
            PluggyCategoryId = pluggyCategoryId,
            PluggyCategory = pluggyCategory,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Transactions.Add(txn);
        db.SaveChanges();
        return txn;
    }

    private static void SeedRule(SimpleFinanceDbContext db, Guid userId, Guid categoryId, string contains)
    {
        var now = DateTime.UtcNow;
        db.CategoryRules.Add(new CategoryRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MatchField = RuleMatchField.Description,
            MatchOperator = RuleMatchOperator.Contains,
            MatchValue = contains,
            CategoryId = categoryId,
            Priority = 1,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
    }

    private static void SeedMapping(SimpleFinanceDbContext db, string pluggyCategoryId, string pluggyCategoryName, Guid categoryId)
    {
        var now = DateTime.UtcNow;
        db.CategoryMappings.Add(new CategoryMapping
        {
            Id = Guid.NewGuid(),
            PluggyCategoryId = pluggyCategoryId,
            PluggyCategoryName = pluggyCategoryName,
            CategoryId = categoryId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
    }

    private static async Task AssertTransactionUntouched(SimpleFinanceDbContext db, Guid txnId)
    {
        // The cascade is a PURE classifier — it must never write the category back onto the txn.
        var fresh = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == txnId);
        Assert.Null(fresh.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, fresh.CategorySource);
        Assert.Null(fresh.CategoryConfidence);
        Assert.Null(fresh.CategoryModel);
    }

    [Fact]
    public async Task Classify_RuleMatch_ReturnsRuleSourceWithFullConfidence_NoAiCalls()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true);
        var categoryId = SeedCategory(db, userId, "Alimentação");
        SeedRule(db, userId, categoryId, "ifood");
        var txn = SeedTransaction(db, userId, "IFD*IFOOD PEDIDO", "iFood");

        var voter = new FakeKnnCategoryVoter();
        var llm = new FakeLlmClient();
        var embedding = new FakeEmbeddingClient();
        var result = await CreateService(db, voter, llm, embedding).ClassifyAsync(userId, txn.Id, CancellationToken.None);

        Assert.Equal(ClassificationSource.Rule, result.Source);
        Assert.Equal(categoryId, result.CategoryId);
        Assert.Equal(1.0, result.Confidence);
        Assert.Null(result.Model);
        // Stop-at-first-hit: no kNN / LLM / embedding calls were made.
        Assert.Equal(0, voter.CallCount);
        Assert.Equal(0, llm.ChatCallCount);
        Assert.Equal(0, embedding.CallCount);
        await AssertTransactionUntouched(db, txn.Id);
    }

    [Fact]
    public async Task Classify_MappingHit_ReturnsMappingSource_NoAiCalls()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true);
        var categoryId = SeedCategory(db, userId, "Alimentação");
        SeedMapping(db, "cat-food", "Food and Drink", categoryId);
        var txn = SeedTransaction(db, userId, "Compra desconhecida", "Loja", pluggyCategoryId: "cat-food", pluggyCategory: "Food and Drink");

        var voter = new FakeKnnCategoryVoter();
        var llm = new FakeLlmClient();
        var embedding = new FakeEmbeddingClient();
        var result = await CreateService(db, voter, llm, embedding).ClassifyAsync(userId, txn.Id, CancellationToken.None);

        Assert.Equal(ClassificationSource.Mapping, result.Source);
        Assert.Equal(categoryId, result.CategoryId);
        Assert.Equal(0.95, result.Confidence);
        Assert.Equal(0, voter.CallCount);
        Assert.Equal(0, llm.ChatCallCount);
        await AssertTransactionUntouched(db, txn.Id);
    }

    [Fact]
    public async Task Classify_NoRuleNoMapping_KnnAboveFloor_ReturnsKnnSourceWithEmbeddingModel()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true);
        var alimentacao = SeedCategory(db, userId, "Alimentação");
        var txn = SeedTransaction(db, userId, "IFD*IFOOD", "iFood");

        var voter = new FakeKnnCategoryVoter { Result = new KnnVote(alimentacao, 0.82) };
        var llm = new FakeLlmClient();
        var embedding = new FakeEmbeddingClient();
        var result = await CreateService(db, voter, llm, embedding).ClassifyAsync(userId, txn.Id, CancellationToken.None);

        Assert.Equal(ClassificationSource.Knn, result.Source);
        Assert.Equal(alimentacao, result.CategoryId);
        Assert.Equal(0.82, result.Confidence);
        Assert.Equal("text-embedding-3-small", result.Model);
        // The query vector was computed on demand (InMemory has no stored embedding) and handed to the voter.
        Assert.Equal(1, embedding.CallCount);
        Assert.Equal(1, voter.CallCount);
        Assert.Equal(0, llm.ChatCallCount); // kNN was confident → LLM never fired (cost lever).
        await AssertTransactionUntouched(db, txn.Id);
    }

    [Fact]
    public async Task Classify_KnnBelowFloor_FallsToLlm_ReturnsLlmSourceWithModelAndRationale()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true, chatModel: "deepseek-chat");
        var alimentacao = SeedCategory(db, userId, "Alimentação");
        var txn = SeedTransaction(db, userId, "IFD*IFOOD", "iFood");

        // Voter returns null → "no signal" (below the proximity floor) → cascade must fall to the LLM.
        var voter = new FakeKnnCategoryVoter { Result = null };
        var llm = new FakeLlmClient
        {
            CannedContent = $"{{\"categoryId\":\"{alimentacao}\",\"confidence\":0.72,\"rationale\":\"iFood é comida\"}}",
        };
        var embedding = new FakeEmbeddingClient();
        var result = await CreateService(db, voter, llm, embedding).ClassifyAsync(userId, txn.Id, CancellationToken.None);

        Assert.Equal(ClassificationSource.Llm, result.Source);
        Assert.Equal(alimentacao, result.CategoryId);
        Assert.Equal(0.72, result.Confidence);
        Assert.Equal("deepseek-chat", result.Model);
        Assert.Equal("iFood é comida", result.Rationale);
        Assert.Equal(1, voter.CallCount);
        Assert.Equal(1, llm.ChatCallCount);
        await AssertTransactionUntouched(db, txn.Id);
    }

    [Fact]
    public async Task Classify_LlmReturnsHallucinatedCategoryId_Rejected_CategoryIdNull()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true);
        SeedCategory(db, userId, "Alimentação");
        var txn = SeedTransaction(db, userId, "Cobrança misteriosa", "Desconhecido");

        // A categoryId that belongs to NO user category → must be rejected (T-16-05).
        var hallucinated = Guid.NewGuid();
        var voter = new FakeKnnCategoryVoter { Result = null };
        var llm = new FakeLlmClient
        {
            CannedContent = $"{{\"categoryId\":\"{hallucinated}\",\"confidence\":0.9,\"rationale\":\"chute\"}}",
        };
        var embedding = new FakeEmbeddingClient();
        var result = await CreateService(db, voter, llm, embedding).ClassifyAsync(userId, txn.Id, CancellationToken.None);

        Assert.Equal(ClassificationSource.Llm, result.Source);
        Assert.Null(result.CategoryId);
        await AssertTransactionUntouched(db, txn.Id);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Classify_AiDisabled_RunsDeterministicOnly_NeverCallsKnnOrLlm(bool aiEnabled, bool categorizationEnabled)
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled, categorizationEnabled);
        var alimentacao = SeedCategory(db, userId, "Alimentação");
        // No rule, no mapping → deterministic returns nothing; AI is off → must degrade to None.
        var txn = SeedTransaction(db, userId, "IFD*IFOOD", "iFood");

        var voter = new FakeKnnCategoryVoter { Result = new KnnVote(alimentacao, 0.99) };
        var llm = new FakeLlmClient();
        var embedding = new FakeEmbeddingClient();
        var result = await CreateService(db, voter, llm, embedding).ClassifyAsync(userId, txn.Id, CancellationToken.None);

        Assert.Equal(ClassificationSource.None, result.Source);
        Assert.Null(result.CategoryId);
        Assert.Equal(0, voter.CallCount);
        Assert.Equal(0, llm.ChatCallCount);
        Assert.Equal(0, embedding.CallCount);
        await AssertTransactionUntouched(db, txn.Id);
    }

    [Fact]
    public async Task Classify_AiDisabled_ButRuleMatches_StillReturnsRule()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: false, categorizationEnabled: false);
        var categoryId = SeedCategory(db, userId, "Alimentação");
        SeedRule(db, userId, categoryId, "ifood");
        var txn = SeedTransaction(db, userId, "IFD*IFOOD PEDIDO", "iFood");

        var voter = new FakeKnnCategoryVoter();
        var llm = new FakeLlmClient();
        var embedding = new FakeEmbeddingClient();
        var result = await CreateService(db, voter, llm, embedding).ClassifyAsync(userId, txn.Id, CancellationToken.None);

        Assert.Equal(ClassificationSource.Rule, result.Source);
        Assert.Equal(categoryId, result.CategoryId);
    }

    [Fact]
    public async Task Classify_BudgetExceededDuringEmbedding_DegradesToNone_NeverThrows()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true);
        SeedCategory(db, userId, "Alimentação");
        var txn = SeedTransaction(db, userId, "IFD*IFOOD", "iFood");

        // Budget trips inside the embedding client (computing the kNN query vector).
        var voter = new FakeKnnCategoryVoter { Result = new KnnVote(Guid.NewGuid(), 0.99) };
        var llm = new FakeLlmClient();
        var embedding = new FakeEmbeddingClient { ThrowBudgetExceeded = true };

        CategoryClassification? result = null;
        var ex = await Record.ExceptionAsync(async () =>
            result = await CreateService(db, voter, llm, embedding).ClassifyAsync(userId, txn.Id, CancellationToken.None));

        Assert.Null(ex);
        Assert.NotNull(result);
        Assert.Equal(ClassificationSource.None, result!.Source);
        Assert.Null(result.CategoryId);
        Assert.Equal(0, voter.CallCount); // never reached the voter — embedding threw first.
        await AssertTransactionUntouched(db, txn.Id);
    }

    [Fact]
    public async Task Classify_UnknownTransaction_ReturnsNone()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true);

        var result = await CreateService(db, new FakeKnnCategoryVoter(), new FakeLlmClient(), new FakeEmbeddingClient())
            .ClassifyAsync(userId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ClassificationSource.None, result.Source);
        Assert.Null(result.CategoryId);
    }

    [Fact]
    public async Task Classify_EmbeddingHttpFailure_SkipsKnn_AndFallsBackToLlm()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true, chatModel: "models/gemini-3.8-flash");
        var alimentacao = SeedCategory(db, userId, "Alimentação");
        var txn = SeedTransaction(db, userId, "IFD*IFOOD", "iFood");

        var voter = new FakeKnnCategoryVoter { Result = new KnnVote(alimentacao, 0.99) };
        var llm = new FakeLlmClient
        {
            CannedContent = $"{{\"categoryId\":\"{alimentacao}\",\"confidence\":0.92,\"rationale\":\"iFood\"}}",
        };
        var embedding = new FakeEmbeddingClient { ThrowHttpRequestException = true };

        var result = await CreateService(db, voter, llm, embedding).ClassifyAsync(userId, txn.Id, CancellationToken.None);

        Assert.Equal(ClassificationSource.Llm, result.Source);
        Assert.Equal(alimentacao, result.CategoryId);
        Assert.Equal(0.92, result.Confidence);
        Assert.Equal("models/gemini-3.8-flash", result.Model);
        Assert.Equal(0, voter.CallCount);
        Assert.Equal(1, llm.ChatCallCount);
    }

    [Fact]
    public async Task Classify_NoEmbeddingKeyConfigured_SkipsEmbeddingAndKnn_AndFallsBackToLlm()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true, chatModel: "models/gemini-3.8-flash");
        var alimentacao = SeedCategory(db, userId, "Alimentação");
        var txn = SeedTransaction(db, userId, "IFD*IFOOD", "iFood");

        var voter = new FakeKnnCategoryVoter { Result = new KnnVote(alimentacao, 0.99) };
        var llm = new FakeLlmClient
        {
            CannedContent = $"{{\"categoryId\":\"{alimentacao}\",\"confidence\":0.91,\"rationale\":\"iFood\"}}",
        };
        var embedding = new FakeEmbeddingClient();

        var result = await CreateService(db, voter, llm, embedding, new EmptyKeyProvider())
            .ClassifyAsync(userId, txn.Id, CancellationToken.None);

        Assert.Equal(ClassificationSource.Llm, result.Source);
        Assert.Equal(alimentacao, result.CategoryId);
        Assert.Equal(0, embedding.CallCount);
        Assert.Equal(0, voter.CallCount);
        Assert.Equal(1, llm.ChatCallCount);
    }

    [Fact]
    public async Task ClassifyBatch_DeduplicatesIdenticalMerchants_SendsSingleBatchedLlmCall_AndRejectsHallucinatedIds()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true, chatModel: "models/gemini-3.5-flash-lite");
        var alimentacao = SeedCategory(db, userId, "Alimentação");
        var transporte = SeedCategory(db, userId, "Transporte");
        var assinaturas = SeedCategory(db, userId, "Assinaturas");
        SeedRule(db, userId, assinaturas, "spotify");

        var spotify = SeedTransaction(db, userId, "SPOTIFY BR", "Spotify");
        var ifood1 = SeedTransaction(db, userId, "IFD*IFOOD", "iFood");
        var ifood2 = SeedTransaction(db, userId, "IFD*IFOOD  ", " iFood ");
        var ifood3 = SeedTransaction(db, userId, "ifd*ifood", "IFOOD");
        var uber1 = SeedTransaction(db, userId, "UBER *TRIP", "Uber");
        var uber2 = SeedTransaction(db, userId, "UBER *TRIP", "Uber");
        var mystery = SeedTransaction(db, userId, "COBRANCA XYZ", "Desconhecido");

        var hallucinated = Guid.NewGuid();
        var voter = new FakeKnnCategoryVoter { Result = null };
        var llm = new FakeLlmClient
        {
            CannedContent = $$"""
                ```json
                [
                  {"id": 0, "categoryId": "{{alimentacao}}", "confidence": 0.94, "rationale": "Delivery de comida"},
                  {"id": 1, "categoryId": "{{transporte}}", "confidence": 0.89, "rationale": "Corrida de app"},
                  {"id": 2, "categoryId": "{{hallucinated}}", "confidence": 0.99, "rationale": "Chute inválido"}
                ]
                ```
                """,
        };
        var embedding = new FakeEmbeddingClient();

        var ids = new[] { spotify.Id, ifood1.Id, ifood2.Id, ifood3.Id, uber1.Id, uber2.Id, mystery.Id };
        var results = await CreateService(db, voter, llm, embedding, new EmptyKeyProvider())
            .ClassifyBatchAsync(userId, ids, CancellationToken.None);

        Assert.Equal(7, results.Count);
        Assert.Equal(1, llm.ChatCallCount); // 6 unresolved transactions collapsed to 3 unique groups in ONE call
        Assert.Equal(0, embedding.CallCount);

        Assert.Equal(ClassificationSource.Rule, results[spotify.Id].Source);
        Assert.Equal(assinaturas, results[spotify.Id].CategoryId);

        foreach (var id in new[] { ifood1.Id, ifood2.Id, ifood3.Id })
        {
            Assert.Equal(ClassificationSource.Llm, results[id].Source);
            Assert.Equal(alimentacao, results[id].CategoryId);
            Assert.Equal(0.94, results[id].Confidence);
        }

        foreach (var id in new[] { uber1.Id, uber2.Id })
        {
            Assert.Equal(ClassificationSource.Llm, results[id].Source);
            Assert.Equal(transporte, results[id].CategoryId);
            Assert.Equal(0.89, results[id].Confidence);
        }

        Assert.Equal(ClassificationSource.Llm, results[mystery.Id].Source);
        Assert.Null(results[mystery.Id].CategoryId);
    }

    [Fact]
    public async Task ClassifyBatch_UsesShortIntegerCategoryIds_ThatSurvivePiiRedactor_AndMapsBackToGuids()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db, aiEnabled: true, categorizationEnabled: true, chatModel: "models/gemini-3.5-flash-lite");
        var alimentacao = SeedCategory(db, userId, "Alimentação");
        var transporte = SeedCategory(db, userId, "Transporte");

        var ifood = SeedTransaction(db, userId, "IFD*IFOOD", "iFood");
        var uber = SeedTransaction(db, userId, "UBER *TRIP", "Uber");
        var invalid = SeedTransaction(db, userId, "LOJA DESCONHECIDA", "Outro");

        var llm = new FakeLlmClient
        {
            CannedContent = """
                [
                  {"id": 0, "categoryId": 1, "confidence": 0.95, "rationale": "Comida"},
                  {"id": 1, "categoryId": 2, "confidence": 0.90, "rationale": "Transporte"},
                  {"id": 2, "categoryId": 99, "confidence": 0.99, "rationale": "Fora do range"}
                ]
                """,
        };

        var results = await CreateService(db, new FakeKnnCategoryVoter(), llm, new FakeEmbeddingClient(), new EmptyKeyProvider())
            .ClassifyBatchAsync(userId, [ifood.Id, uber.Id, invalid.Id], CancellationToken.None);

        // Verify the real PiiRedactor does not corrupt any category ID in the outgoing user prompt.
        var sentUserMessage = llm.LastRequest!.Messages.Last().Content;
        var redactedPrompt = new PiiRedactor().Redact(sentUserMessage);
        Assert.DoesNotContain("REDACTED", redactedPrompt);

        Assert.Equal(alimentacao, results[ifood.Id].CategoryId);
        Assert.Equal(transporte, results[uber.Id].CategoryId);
        Assert.Null(results[invalid.Id].CategoryId);
    }

    private sealed class EmptyKeyProvider : IAiKeyProvider
    {
        public Task<string?> GetDeepSeekKeyAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> GetOpenAiKeyAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> GetKeyAsync(AiProvider provider, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// In-test <see cref="IKnnCategoryVoter"/> substitute: returns a canned <see cref="KnnVote"/>
    /// (or null for "no signal") and records how many times it was consulted, so the cascade tests can
    /// drive level 3 without the pgvector column the InMemory provider cannot map.
    /// </summary>
    private sealed class FakeKnnCategoryVoter : IKnnCategoryVoter
    {
        public KnnVote? Result { get; set; }

        public int CallCount { get; private set; }

        public Task<KnnVote?> VoteAsync(
            Guid userId,
            Guid currentTransactionId,
            Vector query,
            int k,
            double proximityFloorSim,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }
}
