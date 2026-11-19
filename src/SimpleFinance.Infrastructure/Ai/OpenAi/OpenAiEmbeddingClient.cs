using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.OpenAi.Dtos;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.OpenAi;

/// <summary>
/// AI-FND-02: concrete <see cref="IEmbeddingClient"/> over OpenAI's <c>POST /embeddings</c> API
/// (clones the PluggyClient typed-HttpClient shape — hand-rolled System.Text.Json, private wire DTOs
/// mapped into the neutral <c>float[][]</c> so no OpenAI shape leaks past Infrastructure).
///
/// Enforces the same un-bypassable ordering as the LLM client — <b>redact → budget-check → send →
/// record</b>: every input is scrubbed via <see cref="IPiiRedactor"/> before serialization, the
/// <see cref="IAiBudgetGuard"/> runs before any HTTP send, and one <see cref="IAiUsageRecorder"/> row
/// is written per HTTP call. Additionally:
/// <list type="bullet">
/// <item>identical normalized texts are embedded once (dedup by SHA-256 of the normalized text) and
/// the vector is fanned back to every duplicate position — <see cref="HashNormalized"/> is the stable
/// helper Phase 16's persistent cache reuses verbatim;</item>
/// <item>batches are chunked to ≤100 inputs per request (CONTEXT lock);</item>
/// <item>the response <c>data[]</c> is sorted by <c>index</c> before projecting (array order is not
/// guaranteed — RESEARCH Pitfall 4 / T-15-13), keeping vectors aligned to inputs.</item>
/// </list>
/// The model name comes from <see cref="AiOptions.OpenAiEmbeddingModel"/> (config), never a literal.
/// </summary>
public sealed class OpenAiEmbeddingClient : IEmbeddingClient
{
    private const string EmbeddingsPath = "embeddings";
    private const int MaxBatchSize = 100;

    private static readonly JsonSerializerOptions OmitNulls = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;
    private readonly IPiiRedactor _redactor;
    private readonly IAiBudgetGuard _budgetGuard;
    private readonly IAiUsageRecorder _usageRecorder;
    private readonly SimpleFinanceDbContext _db;

    public OpenAiEmbeddingClient(
        HttpClient httpClient,
        IOptions<AiOptions> options,
        IPiiRedactor redactor,
        IAiBudgetGuard budgetGuard,
        IAiUsageRecorder usageRecorder,
        SimpleFinanceDbContext db)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _redactor = redactor;
        _budgetGuard = budgetGuard;
        _usageRecorder = usageRecorder;
        _db = db;
    }

    /// <summary>
    /// Stable normalize-and-hash used to dedup identical texts within a batch (and, in Phase 16, to
    /// key the persistent embedding cache). Trims, collapses internal whitespace to single spaces, and
    /// returns the lowercase hex SHA-256 (RESEARCH Pattern 3). Reused verbatim downstream — do not
    /// change the normalization without invalidating cached hashes.
    /// </summary>
    public static string HashNormalized(string text)
    {
        var normalized = Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var userId = await ResolveUserIdAsync(ct);
        var model = _options.OpenAiEmbeddingModel;

        // 1. redact every input BEFORE anything leaves the VPS.
        var redacted = new string[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            redacted[i] = _redactor.Redact(texts[i]);
        }

        // dedup by SHA-256 of the normalized redacted text: build the unique input list and remember,
        // for each original position, which unique slot it maps to.
        var hashToUniqueIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var uniqueTexts = new List<string>();
        var originalToUnique = new int[texts.Count];
        for (var i = 0; i < redacted.Length; i++)
        {
            var hash = HashNormalized(redacted[i]);
            if (!hashToUniqueIndex.TryGetValue(hash, out var uniqueIndex))
            {
                uniqueIndex = uniqueTexts.Count;
                hashToUniqueIndex[hash] = uniqueIndex;
                uniqueTexts.Add(redacted[i]);
            }

            originalToUnique[i] = uniqueIndex;
        }

        // 2. budget-check BEFORE any HTTP send. 3. send (chunked ≤100). 4. record per call.
        await _budgetGuard.EnsureWithinBudgetAsync(userId, ct);

        var uniqueVectors = new float[uniqueTexts.Count][];
        for (var offset = 0; offset < uniqueTexts.Count; offset += MaxBatchSize)
        {
            var chunk = uniqueTexts.GetRange(offset, Math.Min(MaxBatchSize, uniqueTexts.Count - offset));
            var request = new OpenAiEmbeddingRequest(
                Model: model,
                Input: chunk,
                Dimensions: _options.OpenAiEmbeddingDimensions);

            using var response = await _httpClient.PostAsJsonAsync(EmbeddingsPath, request, OmitNulls, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("OpenAI POST /embeddings returned an empty response body.");

            // sort by the server-declared index (NOT array position) before projecting — the index is
            // relative to THIS chunk's input array, so add the chunk offset to land in the global slot.
            foreach (var item in body.Data.OrderBy(d => d.Index))
            {
                uniqueVectors[offset + item.Index] = item.Embedding;
            }

            var promptTokens = body.Usage?.PromptTokens ?? 0;
            await _usageRecorder.RecordAsync(userId, AiFeature.Embedding, AiProvider.OpenAi, model, promptTokens, 0, ct);
        }

        // fan the deduped vectors back out to every original input position.
        var result = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            result[i] = uniqueVectors[originalToUnique[i]];
        }

        return result;
    }

    /// <summary>
    /// Single-user app: resolve the one owning UserId for budget/usage scoping (mirrors the jobs'
    /// user-resolution). Deterministic (oldest-first).
    /// </summary>
    private async Task<Guid> ResolveUserIdAsync(CancellationToken ct)
    {
        var userId = await _db.Users
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(ct);

        if (userId == Guid.Empty)
        {
            throw new InvalidOperationException("No application user is configured; complete first-run setup before using AI features.");
        }

        return userId;
    }
}
