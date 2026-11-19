using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
public sealed class AiCategorizationService : IBatchAiCategorizationService
{
    /// <summary>Neighbours pulled per kNN query (16-RESEARCH §2 — starting point, Phase 20 tunes).</summary>
    private const int KnnNeighbors = 10;

    /// <summary>Nearest-neighbour similarity floor below which kNN is "no signal" → LLM fallback.</summary>
    private const double ProximityFloorSim = 0.80;

    /// <summary>Confidence assigned to a Pluggy→Category mapping hit (near-certain, but not a user rule).</summary>
    private const double MappingConfidence = 0.95;

    /// <summary>Max unique merchant/description groups sent in a single batched LLM prompt.</summary>
    public const int LlmBatchSize = 40;

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
            // --- Level 3: kNN vote over the user's trusted labels (skip when user has 0 trusted embeddings) ---
            if (await HasTrustedEmbeddingsAsync(userId, ct))
            {
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

    public async Task<IReadOnlyDictionary<Guid, CategoryClassification>> ClassifyBatchAsync(
        Guid userId,
        IReadOnlyList<Guid> transactionIds,
        CancellationToken ct)
    {
        var results = new Dictionary<Guid, CategoryClassification>(transactionIds.Count);
        if (transactionIds.Count == 0)
        {
            return results;
        }

        var transactions = await _db.Transactions
            .Where(t => t.UserId == userId && transactionIds.Contains(t.Id))
            .ToListAsync(ct);

        if (transactions.Count == 0)
        {
            return results;
        }

        var settings = await _db.UserSettings
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        // --- Levels 1-2: deterministic rules + Pluggy mapping (per transaction, respects amount rules) ---
        var unresolved = new List<Transaction>();
        foreach (var txn in transactions)
        {
            var input = new CategorizationInput(
                txn.Description,
                txn.MerchantName,
                txn.Amount,
                txn.PluggyCategoryId,
                txn.PluggyCategory);

            var deterministic = await _deterministic.CategorizeAsync(userId, input, ct);

            if (deterministic.Source == CategorySource.Rule && deterministic.CategoryId is Guid ruleCategory)
            {
                results[txn.Id] = new CategoryClassification(ruleCategory, 1.0, null, ClassificationSource.Rule, null);
                continue;
            }

            if (deterministic.CategoryId is Guid mappingCategory)
            {
                results[txn.Id] = new CategoryClassification(mappingCategory, MappingConfidence, null, ClassificationSource.Mapping, null);
                continue;
            }

            unresolved.Add(txn);
        }

        var aiOn = settings is { AiEnabled: true, AiCategorizationEnabled: true };
        if (!aiOn || unresolved.Count == 0)
        {
            foreach (var txn in unresolved)
            {
                results[txn.Id] = None();
            }

            return results;
        }

        // Deduplicate unresolved transactions by redacted normalized signature so identical merchants
        // (e.g. 200x "IFD*IFOOD") are embedded and sent to the LLM only ONCE.
        var groups = new List<MerchantGroup>();
        var groupByKey = new Dictionary<string, MerchantGroup>(StringComparer.Ordinal);
        foreach (var txn in unresolved)
        {
            var redactedDesc = _redactor.Redact(txn.Description);
            var redactedMerchant = _redactor.Redact(txn.MerchantName);
            var key = BuildGroupKey(redactedDesc, redactedMerchant, txn.PluggyCategoryId, txn.PluggyCategory, txn.Type);

            if (!groupByKey.TryGetValue(key, out var group))
            {
                group = new MerchantGroup(
                    Representative: txn,
                    RedactedDescription: redactedDesc,
                    RedactedMerchant: redactedMerchant,
                    Members: []);
                groupByKey[key] = group;
                groups.Add(group);
            }

            group.Members.Add(txn);
        }

        try
        {
            // --- Level 3: batched kNN vote (only when trusted embeddings exist) ---
            var needLlm = new List<MerchantGroup>();
            var canRunKnn = await HasTrustedEmbeddingsAsync(userId, ct) && await HasEmbeddingKeyAsync(ct);

            if (canRunKnn)
            {
                var vectorsByGroup = await GetBatchQueryVectorsAsync(userId, groups, ct);
                for (var i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    if (vectorsByGroup is not null && vectorsByGroup[i] is Vector query)
                    {
                        var vote = await _voter.VoteAsync(userId, group.Representative.Id, query, KnnNeighbors, ProximityFloorSim, ct);
                        if (vote is not null)
                        {
                            var classification = new CategoryClassification(
                                vote.CategoryId,
                                vote.Confidence,
                                _options.OpenAiEmbeddingModel,
                                ClassificationSource.Knn,
                                null);
                            foreach (var member in group.Members)
                            {
                                results[member.Id] = classification;
                            }

                            continue;
                        }
                    }

                    needLlm.Add(group);
                }
            }
            else
            {
                needLlm.AddRange(groups);
            }

            if (needLlm.Count == 0)
            {
                return results;
            }

            // --- Level 4: batched LLM classification (up to LlmBatchSize unique groups per call) ---
            var categories = await _db.Categories
                .Where(c => c.UserId == userId && !c.IsHidden)
                .Select(c => new CategoryRef(c.Id, c.Name, c.Type.ToString()))
                .ToListAsync(ct);

            if (categories.Count == 0)
            {
                foreach (var group in needLlm)
                {
                    foreach (var member in group.Members)
                    {
                        results[member.Id] = None();
                    }
                }

                return results;
            }

            var validCategoryIds = categories.Select(c => c.Id).ToHashSet();
            var model = settings!.AiChatModel;

            for (var offset = 0; offset < needLlm.Count; offset += LlmBatchSize)
            {
                var slice = needLlm.GetRange(offset, Math.Min(LlmBatchSize, needLlm.Count - offset));
                var request = new LlmChatRequest(
                    model,
                    BuildBatchMessages(slice, categories),
                    AiFeature.Categorization);

                var llmResponse = await _llmClient.ChatAsync(request, ct);
                var parsed = ParseBatchLlmJson(llmResponse.Content, categories, validCategoryIds);

                var resolvedGroups = 0;
                for (var idx = 0; idx < slice.Count; idx++)
                {
                    var group = slice[idx];
                    CategoryClassification decision;
                    if (parsed.TryGetValue(idx, out var item)
                        && item.CategoryId is Guid candidate
                        && validCategoryIds.Contains(candidate))
                    {
                        resolvedGroups++;
                        decision = new CategoryClassification(
                            candidate,
                            Math.Clamp(item.Confidence, 0.0, 1.0),
                            model,
                            ClassificationSource.Llm,
                            item.Rationale);
                    }
                    else
                    {
                        decision = new CategoryClassification(
                            null,
                            parsed.TryGetValue(idx, out var rejected) ? Math.Clamp(rejected.Confidence, 0.0, 1.0) : 0.0,
                            model,
                            ClassificationSource.Llm,
                            parsed.TryGetValue(idx, out var r) ? r.Rationale : null);
                    }

                    foreach (var member in group.Members)
                    {
                        results[member.Id] = decision;
                    }
                }

                if (resolvedGroups == 0)
                {
                    _logger.LogWarning(
                        "AiCategorizationService batch LLM call resolved 0/{GroupCount} groups (parsed {ParsedCount} entries, response length {Length}).",
                        slice.Count,
                        parsed.Count,
                        llmResponse.Content?.Length ?? 0);
                }
            }
        }
        catch (AiBudgetExceededException ex)
        {
            _logger.LogWarning(
                ex,
                "AiCategorizationService batch degraded to deterministic-only for user {UserId}: AI monthly budget reached.",
                userId);

            foreach (var txn in unresolved)
            {
                results.TryAdd(txn.Id, None());
            }
        }

        return results;
    }

    private async Task<bool> HasTrustedEmbeddingsAsync(Guid userId, CancellationToken ct)
    {
        // Under EF InMemory (unit tests), TransactionEmbeddings is not mapped and FakeKnnCategoryVoter
        // handles level 3 directly. Under real Postgres (Npgsql), avoid calling /embeddings when the
        // user has zero trusted embeddings for kNN to vote over.
        if (!_db.Database.IsNpgsql())
        {
            return true;
        }

        return await (
                from te in _db.TransactionEmbeddings
                join t in _db.Transactions on te.TransactionId equals t.Id
                where te.UserId == userId
                      && t.CategoryId != null
                      && (t.CategorySource == CategorySource.Rule
                          || t.CategorySource == CategorySource.UserOverride
                          || t.CategorySource == CategorySource.AiSuggested)
                select te.Id)
            .AnyAsync(ct);
    }

    private async Task<bool> HasEmbeddingKeyAsync(CancellationToken ct)
    {
        if (_keyProvider is null)
        {
            return true;
        }

        var openAiKey = await _keyProvider.GetOpenAiKeyAsync(ct);
        return !string.IsNullOrWhiteSpace(openAiKey);
    }

    private async Task<Vector?[]?> GetBatchQueryVectorsAsync(
        Guid userId,
        IReadOnlyList<MerchantGroup> groups,
        CancellationToken ct)
    {
        var vectors = new Vector?[groups.Count];
        var missingIndices = new List<int>();
        var missingTexts = new List<string>();

        Dictionary<Guid, Vector>? storedByTxn = null;
        if (_db.Database.IsNpgsql())
        {
            var repIds = groups.Select(g => g.Representative.Id).ToList();
            storedByTxn = await _db.TransactionEmbeddings
                .Where(te => te.UserId == userId && repIds.Contains(te.TransactionId))
                .ToDictionaryAsync(te => te.TransactionId, te => te.Embedding, ct);
        }

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (storedByTxn is not null && storedByTxn.TryGetValue(group.Representative.Id, out var stored))
            {
                vectors[i] = stored;
            }
            else
            {
                missingIndices.Add(i);
                missingTexts.Add($"{group.RedactedDescription} {group.RedactedMerchant}".Trim());
            }
        }

        if (missingTexts.Count > 0)
        {
            try
            {
                var computed = await _embeddingClient.EmbedAsync(missingTexts, ct);
                for (var j = 0; j < missingIndices.Count && j < computed.Length; j++)
                {
                    vectors[missingIndices[j]] = new Vector(computed[j]);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "AiCategorizationService skipped batch kNN embeddings for user {UserId}; falling back to LLM.",
                    userId);
                return null;
            }
        }

        return vectors;
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
            .Select(c => new CategoryRef(c.Id, c.Name, c.Type.ToString()))
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
        var validCategoryIds = categories.Select(c => c.Id).ToHashSet();
        var parsed = ParseLlmJson(result.Content, categories, validCategoryIds);

        // T-16-05: reject any categoryId the model invented that is not one of THIS user's categories.
        if (parsed?.CategoryId is Guid candidate && validCategoryIds.Contains(candidate))
        {
            return new CategoryClassification(candidate, parsed.Confidence, model, ClassificationSource.Llm, parsed.Rationale);
        }

        return new CategoryClassification(null, parsed?.Confidence ?? 0.0, model, ClassificationSource.Llm, parsed?.Rationale);
    }

    private IReadOnlyList<LlmMessage> BuildMessages(Transaction txn, IReadOnlyList<CategoryRef> categories)
    {
        // Use 1-based integer ids for categories on the wire: OpenAiCompatibleLlmClient runs
        // PiiRedactor.Redact over every outbound message, and PiiRedactor.TokenLike() replaces
        // 32+ char UUIDs with "«REDACTED»". Integer ids survive redaction intact and save tokens.
        const string system =
            "You categorize a Brazilian bank transaction into exactly one of the user's categories. " +
            "Reply ONLY with a JSON object using the integer \"id\" from \"categories\": " +
            "{\"categoryId\": <integer id or null>, \"confidence\": 0..1, \"rationale\": \"<short pt-BR>\"}. " +
            "Use null for categoryId if none fits.";

        // T-16-04: redact the free-text fields BEFORE they enter the prompt (defense in depth on top of
        // the client's own redaction). Only category index + name leave the VPS — no account/CPF/UUID fields.
        var description = _redactor.Redact(txn.Description);
        var merchant = _redactor.Redact(txn.MerchantName);

        var categoryList = JsonSerializer.Serialize(
            categories.Select((c, i) => new { id = i + 1, name = c.Name, type = c.Type }));

        var user = new StringBuilder()
            .Append("{\"transaction\":{")
            .Append("\"description\":").Append(JsonSerializer.Serialize(description)).Append(',')
            .Append("\"merchant\":").Append(JsonSerializer.Serialize(merchant)).Append(',')
            .Append("\"amount\":").Append(JsonSerializer.Serialize(txn.Amount)).Append(',')
            .Append("\"type\":").Append(JsonSerializer.Serialize(txn.Type == TransactionType.Credit ? "income" : "expense")).Append(',')
            .Append("\"pluggyCategory\":").Append(JsonSerializer.Serialize(txn.PluggyCategory))
            .Append("},\"categories\":").Append(categoryList).Append('}')
            .ToString();

        return new[]
        {
            new LlmMessage("system", system),
            new LlmMessage("user", user),
        };
    }

    private static IReadOnlyList<LlmMessage> BuildBatchMessages(
        IReadOnlyList<MerchantGroup> groups,
        IReadOnlyList<CategoryRef> categories)
    {
        // 1-based integer category ids survive PiiRedactor.TokenLike() (which masks 32+ char UUIDs).
        const string system =
            "You categorize Brazilian bank transactions into the user's categories. " +
            "For each transaction (identified by integer \"id\"), choose the single best matching integer \"id\" from \"categories\" " +
            "(respecting Expense vs Income type), or null if none fits. " +
            "Reply ONLY with a JSON array containing one object per transaction: " +
            "[{\"id\": 0, \"categoryId\": 1, \"confidence\": 0.95, \"rationale\": \"<short pt-BR>\"}].";

        var payload = new
        {
            categories = categories.Select((c, i) => new { id = i + 1, name = c.Name, type = c.Type }),
            transactions = groups.Select((g, i) => new
            {
                id = i,
                description = g.RedactedDescription,
                merchant = g.RedactedMerchant,
                amount = g.Representative.Amount,
                type = g.Representative.Type == TransactionType.Credit ? "income" : "expense",
                pluggyCategory = g.Representative.PluggyCategory,
            }),
        };

        return
        [
            new LlmMessage("system", system),
            new LlmMessage("user", JsonSerializer.Serialize(payload)),
        ];
    }

    /// <summary>
    /// Defensively extracts the first JSON object from the model's answer (it may wrap the JSON in
    /// prose or fences) and reads categoryId/confidence/rationale. Returns null when unparseable.
    /// </summary>
    private static LlmClassification? ParseLlmJson(
        string? content,
        IReadOnlyList<CategoryRef> categories,
        HashSet<Guid> validCategoryIds)
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
            return ReadClassification(doc.RootElement, categories, validCategoryIds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Defensively extracts the batch JSON array from the model's answer (handling bare arrays,
    /// markdown fences, or object wrappers like <c>{"results":[...]}</c>). Returns a map of item
    /// index → classification; malformed/missing entries are omitted.
    /// </summary>
    private static Dictionary<int, LlmClassification> ParseBatchLlmJson(
        string? content,
        IReadOnlyList<CategoryRef> categories,
        HashSet<Guid> validCategoryIds)
    {
        var map = new Dictionary<int, LlmClassification>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return map;
        }

        var trimmed = StripCodeFences(content);

        JsonDocument? doc = null;
        try
        {
            try
            {
                doc = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                var start = trimmed.IndexOf('[');
                var end = trimmed.LastIndexOf(']');
                if (start < 0 || end <= start)
                {
                    return map;
                }

                doc = JsonDocument.Parse(trimmed.Substring(start, end - start + 1));
            }

            JsonElement arrayEl = default;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                arrayEl = doc.RootElement;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        arrayEl = prop.Value;
                        break;
                    }
                }
            }

            if (arrayEl.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (var item in arrayEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("id", out var idxEl))
                {
                    continue;
                }

                int idx;
                if (idxEl.ValueKind == JsonValueKind.Number && idxEl.TryGetInt32(out var numIdx))
                {
                    idx = numIdx;
                }
                else if (idxEl.ValueKind == JsonValueKind.String && int.TryParse(idxEl.GetString(), out var strIdx))
                {
                    idx = strIdx;
                }
                else
                {
                    continue;
                }

                map[idx] = ReadClassification(item, categories, validCategoryIds);
            }
        }
        catch (JsonException)
        {
            // Malformed LLM output — return whatever (if anything) was valid.
        }
        finally
        {
            doc?.Dispose();
        }

        return map;
    }

    private static string StripCodeFences(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }

        text = text[(firstNewline + 1)..];
        var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            text = text[..closingFence];
        }

        return text.Trim();
    }

    private static LlmClassification ReadClassification(
        JsonElement root,
        IReadOnlyList<CategoryRef> categories,
        HashSet<Guid> validCategoryIds)
    {
        Guid? categoryId = null;
        if (root.TryGetProperty("categoryId", out var idEl))
        {
            if (idEl.ValueKind == JsonValueKind.Number
                && idEl.TryGetInt32(out var numId)
                && numId >= 1
                && numId <= categories.Count)
            {
                categoryId = categories[numId - 1].Id;
            }
            else if (idEl.ValueKind == JsonValueKind.String)
            {
                var raw = idEl.GetString()?.Trim();
                if (int.TryParse(raw, out var strNumId)
                    && strNumId >= 1
                    && strNumId <= categories.Count)
                {
                    categoryId = categories[strNumId - 1].Id;
                }
                else if (Guid.TryParse(raw, out var g) && validCategoryIds.Contains(g))
                {
                    categoryId = g;
                }
            }
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

    private static string BuildGroupKey(
        string? redactedDescription,
        string? redactedMerchant,
        string? pluggyCategoryId,
        string? pluggyCategory,
        TransactionType type) =>
        string.Concat(
            NormalizeKeyPart(redactedDescription),
            "|",
            NormalizeKeyPart(redactedMerchant),
            "|",
            NormalizeKeyPart(pluggyCategoryId),
            "|",
            NormalizeKeyPart(pluggyCategory),
            "|",
            (int)type);

    private static string NormalizeKeyPart(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static CategoryClassification None() =>
        new(null, 0.0, null, ClassificationSource.None, null);

    private sealed record CategoryRef(Guid Id, string Name, string Type);

    private sealed record LlmClassification(Guid? CategoryId, double Confidence, string? Rationale);

    private sealed record MerchantGroup(
        Transaction Representative,
        string RedactedDescription,
        string RedactedMerchant,
        List<Transaction> Members);
}
