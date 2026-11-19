using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Application.Categorization;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Categorization;

/// <summary>
/// AI-CAT-02/03: the 4-level read-only categorization cascade (see
/// <see cref="IAiCategorizationService"/>). Reuses the existing deterministic
/// <see cref="ICategorizationService"/> (<c>CategoryRuleEngine</c>) for levels 1-2, then — only when
/// the user has AI + categorization enabled — a pgvector kNN vote (level 3) and finally a
/// <c>deepseek-chat</c> strict-JSON call (level 4). It returns a pure
/// <see cref="CategoryClassification"/> and NEVER writes to the transaction or creates a suggestion.
///
/// Cost + safety levers: the LLM only fires when rules+mapping+kNN all fail; PII is redacted before
/// the prompt is built (T-16-04, on top of the client's own redaction); a hallucinated categoryId is
/// rejected against the user's own category list (T-16-05); and an <see cref="AiBudgetExceededException"/>
/// thrown inside the embedding/LLM clients is caught so the cascade degrades to the best deterministic
/// result and never crashes (T-16-07).
/// </summary>
public sealed class AiCategorizationService : IAiCategorizationService
{
    /// <summary>Neighbours pulled per kNN query (16-RESEARCH §2 — starting point, Phase 20 tunes).</summary>
    private const int KnnNeighbors = 10;

    /// <summary>Nearest-neighbour similarity floor below which kNN is "no signal" → LLM fallback.</summary>
    private const double ProximityFloorSim = 0.80;

    /// <summary>Confidence assigned to a Pluggy→Category mapping hit (near-certain, but not a user rule).</summary>
    private const double MappingConfidence = 0.95;

    private readonly SimpleFinanceDbContext _db;
    private readonly ICategorizationService _deterministic;
    private readonly IKnnCategoryVoter _voter;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ILlmClient _llmClient;
    private readonly IPiiRedactor _redactor;
    private readonly AiOptions _options;
    private readonly ILogger<AiCategorizationService> _logger;
    private readonly IAiKeyProvider? _keyProvider;

    public AiCategorizationService(
        SimpleFinanceDbContext db,
        ICategorizationService deterministic,
        IKnnCategoryVoter voter,
        IEmbeddingClient embeddingClient,
        ILlmClient llmClient,
        IPiiRedactor redactor,
        IOptions<AiOptions> options,
        ILogger<AiCategorizationService> logger,
        IAiKeyProvider? keyProvider = null)
    {
        _db = db;
        _deterministic = deterministic;
        _voter = voter;
        _embeddingClient = embeddingClient;
        _llmClient = llmClient;
        _redactor = redactor;
        _options = options.Value;
        _logger = logger;
        _keyProvider = keyProvider;
    }

    public async Task<CategoryClassification> ClassifyAsync(Guid userId, Guid transactionId, CancellationToken ct)
    {
        var txn = await _db.Transactions
            .FirstOrDefaultAsync(t => t.UserId == userId && t.Id == transactionId, ct);

        if (txn is null)
        {
            return None();
        }

        var settings = await _db.UserSettings
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        // --- Levels 1-2: deterministic rules + Pluggy mapping (cheap, no network, always run) ---
        var input = new CategorizationInput(
            txn.Description,
            txn.MerchantName,
            txn.Amount,
            txn.PluggyCategoryId,
            txn.PluggyCategory);

        var deterministic = await _deterministic.CategorizeAsync(userId, input, ct);

        if (deterministic.Source == CategorySource.Rule && deterministic.CategoryId is Guid ruleCategory)
        {
            return new CategoryClassification(ruleCategory, 1.0, null, ClassificationSource.Rule, null);
        }

        if (deterministic.CategoryId is Guid mappingCategory)
        {
            return new CategoryClassification(mappingCategory, MappingConfidence, null, ClassificationSource.Mapping, null);
        }

        // --- Graceful degradation: only continue to the paid AI levels when both toggles are on ---
        var aiOn = settings is { AiEnabled: true, AiCategorizationEnabled: true };
        if (!aiOn)
        {
            return None();
        }

        try
        {
            // --- Level 3: kNN vote over the user's trusted labels ---
            var query = await GetQueryVectorAsync(userId, txn, ct);
            if (query is not null)
            {
                var vote = await _voter.VoteAsync(userId, txn.Id, query, KnnNeighbors, ProximityFloorSim, ct);
                if (vote is not null)
                {
                    return new CategoryClassification(
                        vote.CategoryId,
                        vote.Confidence,
                        _options.OpenAiEmbeddingModel,
                        ClassificationSource.Knn,
                        null);
                }
            }

            // --- Level 4: LLM strict-JSON fallback (rare — the cost lever) ---
            return await ClassifyWithLlmAsync(userId, txn, settings!, ct);
        }
        catch (AiBudgetExceededException ex)
        {
            // Expected degrade, not a failure: rules+mapping already had their chance (returned None),
            // so the best deterministic result here is None. Never rethrow.
            _logger.LogWarning(
                ex,
                "AiCategorizationService degraded to deterministic-only for user {UserId}, transaction {TransactionId}: AI monthly budget reached.",
                userId,
                transactionId);
            return None();
        }
    }

    /// <summary>
    /// Resolves the query embedding: prefer the stored <see cref="TransactionEmbedding"/> (only readable
    /// under Npgsql — the entity is Ignored on InMemory), else compute it on demand from the same
    /// redacted-text recipe the embedding job uses, so the vectors are directly comparable.
    /// </summary>
    private async Task<Vector?> GetQueryVectorAsync(Guid userId, Transaction txn, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
        {
            var stored = await _db.TransactionEmbeddings
                .Where(te => te.UserId == userId && te.TransactionId == txn.Id)
                .Select(te => te.Embedding)
                .FirstOrDefaultAsync(ct);

            if (stored is not null)
            {
                return stored;
            }
        }

        if (_keyProvider is not null)
        {
            var openAiKey = await _keyProvider.GetOpenAiKeyAsync(ct);
            if (string.IsNullOrWhiteSpace(openAiKey))
            {
                return null;
            }
        }

        var text = _redactor.Redact($"{txn.Description} {txn.MerchantName}".Trim());
        try
        {
            var vectors = await _embeddingClient.EmbedAsync(new[] { text }, ct);
            return vectors.Length > 0 ? new Vector(vectors[0]) : null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "AiCategorizationService skipped kNN embedding for user {UserId}, transaction {TransactionId}; falling back to LLM.",
                userId,
                txn.Id);
            return null;
        }
    }

    private async Task<CategoryClassification> ClassifyWithLlmAsync(Guid userId, Transaction txn, UserSettings settings, CancellationToken ct)
    {
        var categories = await _db.Categories
            .Where(c => c.UserId == userId && !c.IsHidden)
            .Select(c => new CategoryRef(c.Id, c.Name))
            .ToListAsync(ct);

        if (categories.Count == 0)
        {
            return None();
        }

        var model = settings.AiChatModel;
        var request = new LlmChatRequest(
            model,
            BuildMessages(txn, categories),
            AiFeature.Categorization);

        var result = await _llmClient.ChatAsync(request, ct);
        var parsed = ParseLlmJson(result.Content);

        // T-16-05: reject any categoryId the model invented that is not one of THIS user's categories.
        if (parsed?.CategoryId is Guid candidate && categories.Any(c => c.Id == candidate))
        {
            return new CategoryClassification(candidate, parsed.Confidence, model, ClassificationSource.Llm, parsed.Rationale);
        }

        return new CategoryClassification(null, parsed?.Confidence ?? 0.0, model, ClassificationSource.Llm, parsed?.Rationale);
    }

    private IReadOnlyList<LlmMessage> BuildMessages(Transaction txn, IReadOnlyList<CategoryRef> categories)
    {
        const string system =
            "You categorize a Brazilian bank transaction into exactly one of the user's categories. " +
            "Reply ONLY with a JSON object: {\"categoryId\": \"<id or null>\", \"confidence\": 0..1, " +
            "\"rationale\": \"<short pt-BR>\"}. Use null for categoryId if none fits.";

        // T-16-04: redact the free-text fields BEFORE they enter the prompt (defense in depth on top of
        // the client's own redaction). Only category ids + names leave the VPS — no account/CPF fields.
        var description = _redactor.Redact(txn.Description);
        var merchant = _redactor.Redact(txn.MerchantName);

        var categoryList = JsonSerializer.Serialize(
            categories.Select(c => new { id = c.Id, name = c.Name }));

        var user = new StringBuilder()
            .Append("{\"transaction\":{")
            .Append("\"description\":").Append(JsonSerializer.Serialize(description)).Append(',')
            .Append("\"merchant\":").Append(JsonSerializer.Serialize(merchant)).Append(',')
            .Append("\"amount\":").Append(JsonSerializer.Serialize(txn.Amount)).Append(',')
            .Append("\"pluggyCategory\":").Append(JsonSerializer.Serialize(txn.PluggyCategory))
            .Append("},\"categories\":").Append(categoryList).Append('}')
            .ToString();

        return new[]
        {
            new LlmMessage("system", system),
            new LlmMessage("user", user),
        };
    }

    /// <summary>
    /// Defensively extracts the first JSON object from the model's answer (it may wrap the JSON in
    /// prose or fences) and reads categoryId/confidence/rationale. Returns null when unparseable.
    /// </summary>
    private static LlmClassification? ParseLlmJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        var json = content.Substring(start, end - start + 1);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Guid? categoryId = null;
            if (root.TryGetProperty("categoryId", out var idEl)
                && idEl.ValueKind == JsonValueKind.String
                && Guid.TryParse(idEl.GetString(), out var g))
            {
                categoryId = g;
            }

            var confidence = 0.0;
            if (root.TryGetProperty("confidence", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
            {
                confidence = cEl.GetDouble();
            }

            string? rationale = null;
            if (root.TryGetProperty("rationale", out var rEl) && rEl.ValueKind == JsonValueKind.String)
            {
                rationale = rEl.GetString();
            }

            return new LlmClassification(categoryId, confidence, rationale);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CategoryClassification None() =>
        new(null, 0.0, null, ClassificationSource.None, null);

    private sealed record CategoryRef(Guid Id, string Name);

    private sealed record LlmClassification(Guid? CategoryId, double Confidence, string? Rationale);
}
