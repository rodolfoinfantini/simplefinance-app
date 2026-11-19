using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.OpenAi;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// AI-CAT-01: the nightly Hangfire job (also enqueued post-sync in 16-04) that vectorizes
/// transactions into <see cref="TransactionEmbedding"/> rows so the 16-03 kNN cascade has something
/// to compare against. For every user with AI + categorization enabled, it finds a bounded page of
/// transactions that have no embedding yet, builds the source text = <c>PiiRedactor.Redact(Description
/// + " " + MerchantName)</c> (PII is scrubbed BEFORE hashing/sending — T-16-01, defense in depth on
/// top of the client's own redaction), keys each by
/// <see cref="OpenAiEmbeddingClient.HashNormalized(string)"/>, serves any hash already present in the
/// user's embeddings straight from the persistent cache (copies the vector, no API call), and embeds
/// the remaining distinct hashes in chunks of ≤100 via <see cref="IEmbeddingClient"/>.
///
/// Cost is bounded three ways: a per-user page cap, the (UserId, SourceTextHash) cache, and the
/// budget guard inside the embedding client — when it throws <see cref="AiBudgetExceededException"/>
/// the job logs, persists whatever it has already resolved (cache copies + completed chunks) and
/// stops gracefully, leaving the rest for the next run (T-16-02). It never rethrows, so it can never
/// crash a post-sync trigger.
/// </summary>
public sealed class AiEmbeddingJob
{
    /// <summary>Max transactions embedded per user per run — keeps a single run cheap/bounded.</summary>
    private const int PageSize = 500;

    /// <summary>Matches the embedding client's own request cap (CONTEXT lock).</summary>
    private const int MaxBatchSize = 100;

    private readonly SimpleFinanceDbContext _db;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly IPiiRedactor _redactor;
    private readonly AiOptions _options;
    private readonly ILogger<AiEmbeddingJob> _logger;
    private readonly IAiKeyProvider? _keyProvider;

    public AiEmbeddingJob(
        SimpleFinanceDbContext db,
        IEmbeddingClient embeddingClient,
        IPiiRedactor redactor,
        IOptions<AiOptions> options,
        ILogger<AiEmbeddingJob> logger,
        IAiKeyProvider? keyProvider = null)
    {
        _db = db;
        _embeddingClient = embeddingClient;
        _redactor = redactor;
        _options = options.Value;
        _logger = logger;
        _keyProvider = keyProvider;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        // Only users who opted into cloud AI categorization are ever processed. In the single-user
        // app this is the one owner; iterating keeps the job correct and test-isolatable.
        var userIds = await _db.UserSettings
            .Where(s => s.AiEnabled && s.AiCategorizationEnabled)
            .Select(s => s.UserId)
            .ToListAsync(ct);

        foreach (var userId in userIds)
        {
            await EmbedUserAsync(userId, ct);
        }
    }

    private async Task EmbedUserAsync(Guid userId, CancellationToken ct)
    {
        // Find work: a bounded page of this user's non-deleted transactions with no embedding row yet.
        // The global soft-delete query filter already excludes DeletedAt != null on both sides.
        var transactions = await _db.Transactions
            .Where(t => t.UserId == userId
                        && !_db.TransactionEmbeddings.Any(te => te.TransactionId == t.Id))
            .OrderBy(t => t.CreatedAt)
            .Take(PageSize)
            .ToListAsync(ct);

        if (transactions.Count == 0)
        {
            return;
        }

        // Compute the redacted source text + cache hash for each transaction.
        var model = _options.OpenAiEmbeddingModel;
        var txnHash = new Dictionary<Guid, string>();
        var hashText = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in transactions)
        {
            var text = _redactor.Redact($"{t.Description} {t.MerchantName}".Trim());
            var hash = OpenAiEmbeddingClient.HashNormalized(text);
            txnHash[t.Id] = hash;
            hashText[hash] = text;
        }

        // Persistent cache: any hash already embedded for this user is reused (copied), never re-sent.
        var neededHashes = hashText.Keys.ToList();
        var cached = await _db.TransactionEmbeddings
            .Where(te => te.UserId == userId && neededHashes.Contains(te.SourceTextHash))
            .ToListAsync(ct);

        var hashToVector = new Dictionary<string, Pgvector.Vector>(StringComparer.Ordinal);
        foreach (var row in cached)
        {
            hashToVector.TryAdd(row.SourceTextHash, row.Embedding);
        }

        // The distinct hashes still needing a real embedding call.
        var toEmbed = neededHashes.Where(h => !hashToVector.ContainsKey(h)).ToList();

        if (toEmbed.Count > 0 && _keyProvider is not null)
        {
            var openAiKey = await _keyProvider.GetOpenAiKeyAsync(ct);
            if (string.IsNullOrWhiteSpace(openAiKey))
            {
                _logger.LogInformation(
                    "AiEmbeddingJob skipped remote embeddings for user {UserId}: no embedding API key configured.",
                    userId);
                toEmbed.Clear();
            }
        }

        var budgetHit = false;
        for (var offset = 0; offset < toEmbed.Count; offset += MaxBatchSize)
        {
            var chunk = toEmbed.GetRange(offset, Math.Min(MaxBatchSize, toEmbed.Count - offset));
            var texts = chunk.Select(h => hashText[h]).ToList();

            try
            {
                var vectors = await _embeddingClient.EmbedAsync(texts, ct);
                for (var i = 0; i < chunk.Count; i++)
                {
                    hashToVector[chunk[i]] = new Pgvector.Vector(vectors[i]);
                }
            }
            catch (AiBudgetExceededException ex)
            {
                // Expected degrade, not a failure: keep what earlier chunks + the cache produced and
                // leave the remainder for the next run. Never rethrow (must not crash a post-sync run).
                _logger.LogWarning(
                    ex,
                    "AiEmbeddingJob stopped early for user {UserId}: AI monthly budget reached. {Done} of {Total} distinct texts embedded this run; remaining left for next run.",
                    userId,
                    offset,
                    toEmbed.Count);
                budgetHit = true;
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "AiEmbeddingJob stopped early for user {UserId}: embedding provider request failed. {Done} of {Total} distinct texts embedded this run; remaining left for next run.",
                    userId,
                    offset,
                    toEmbed.Count);
                break;
            }
        }

        // Create one embedding row per transaction whose hash resolved to a vector (cache-hit or freshly
        // embedded). Transactions whose hash was skipped by the budget break are left for the next run.
        var now = DateTime.UtcNow;
        foreach (var t in transactions)
        {
            if (!hashToVector.TryGetValue(txnHash[t.Id], out var vector))
            {
                continue;
            }

            _db.TransactionEmbeddings.Add(new TransactionEmbedding
            {
                UserId = userId,
                TransactionId = t.Id,
                Embedding = vector,
                EmbeddingModel = model,
                SourceTextHash = txnHash[t.Id],
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);

        if (budgetHit)
        {
            _logger.LogInformation("AiEmbeddingJob persisted the resolved embeddings for user {UserId} before stopping on budget.", userId);
        }
    }
}
