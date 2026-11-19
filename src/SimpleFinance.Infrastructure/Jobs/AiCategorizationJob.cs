using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleFinance.Application.Categorization;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Categorization;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// AI-CAT-03/04/05: the Hangfire job (nightly + enqueued post-sync in 16-03's sync seam + on-demand
/// via 16-05's POST /api/ai/categorize/run) that turns the pure 16-03 cascade into a running,
/// reversible categorization loop over the "Sem categoria" backlog.
///
/// For every user with AI + categorization enabled it iterates a bounded page of genuinely
/// uncategorized transactions (<c>CategoryId == null</c>) that carry NO trusted provenance —
/// <c>CategorySource</c> is never one of Rule/UserOverride/AiSuggested/AiAuto (AI-CAT-04, the
/// never-overwrite rule: those rows are never even classified). Each is de-duplicated against any
/// existing Pending <see cref="AiSuggestion"/> (idempotency — a second run never double-queues), then
/// classified via <see cref="IAiCategorizationService.ClassifyAsync"/>. The decision is applied by the
/// hybrid-by-confidence rule (AI-CAT-03):
/// <list type="bullet">
///   <item>CategoryId resolved AND Confidence ≥ <c>UserSettings.AiAutoApplyThreshold</c> ⇒ auto-apply:
///   set <c>CategoryId</c>, <c>CategorySource=AiAuto</c>, and record <c>CategoryConfidence</c>/<c>CategoryModel</c>.</item>
///   <item>CategoryId resolved but below threshold ⇒ create a Pending <see cref="AiSuggestion"/>
///   (Type=Category, <c>{"categoryId":"…"}</c> payload) and leave the transaction UNTOUCHED.</item>
///   <item>No category / <c>ClassificationSource.None</c> ⇒ leave it "Sem categoria".</item>
/// </list>
/// The cascade already degrades to deterministic-only when AI is off / over budget, so this job never
/// crashes a post-sync trigger.
/// </summary>
public sealed class AiCategorizationJob
{
    /// <summary>Max transactions classified per user per run on the single-transaction fallback path.</summary>
    private const int PageSize = 500;

    /// <summary>Max transactions processed per run on the grouped/batched path.</summary>
    private const int BatchBacklogLimit = 10_000;

    /// <summary>Number of unique merchant groups sent per batch call (matches LLM batch size).</summary>
    private const int UniqueGroupsPerChunk = AiCategorizationService.LlmBatchSize;

    /// <summary>Max consecutive 429 rate-limit retries per batch before stopping gracefully.</summary>
    private const int MaxRateLimitRetries = 3;

    /// <summary>Wait window after a 429 Too Many Requests so per-minute free-tier quotas reset.</summary>
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(65);

    /// <summary>Trusted/AI-owned provenances the job must never re-classify or overwrite (AI-CAT-04).</summary>
    private static readonly CategorySource[] ProtectedSources =
    [
        CategorySource.Rule,
        CategorySource.UserOverride,
        CategorySource.AiSuggested,
        CategorySource.AiAuto,
    ];

    private readonly SimpleFinanceDbContext _db;
    private readonly IAiCategorizationService _classifier;
    private readonly ILogger<AiCategorizationJob> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AiCategorizationJob(
        SimpleFinanceDbContext db,
        IAiCategorizationService classifier,
        ILogger<AiCategorizationJob> logger)
        : this(db, classifier, logger, Task.Delay)
    {
    }

    internal AiCategorizationJob(
        SimpleFinanceDbContext db,
        IAiCategorizationService classifier,
        ILogger<AiCategorizationJob> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _db = db;
        _classifier = classifier;
        _logger = logger;
        _delay = delay;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        // Only users who opted into cloud AI categorization are ever processed (single-user app: the
        // one owner). Iterating keeps the job correct and test-isolatable.
        var userIds = await _db.UserSettings
            .Where(s => s.AiEnabled && s.AiCategorizationEnabled)
            .Select(s => s.UserId)
            .ToListAsync(ct);

        foreach (var userId in userIds)
        {
            await CategorizeUserAsync(userId, ct);
        }
    }

    private async Task CategorizeUserAsync(Guid userId, CancellationToken ct)
    {
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (settings is null)
        {
            return;
        }

        var threshold = settings.AiAutoApplyThreshold;
        var limit = _classifier is IBatchAiCategorizationService ? BatchBacklogLimit : PageSize;

        // Backlog: genuinely uncategorized, non-protected rows. The global soft-delete query filter
        // already excludes DeletedAt != null. Excluding ProtectedSources enforces never-overwrite
        // BEFORE any classification call (AI-CAT-04) — a Rule/UserOverride/AiSuggested/AiAuto row is
        // never even read into the cascade.
        var backlog = await _db.Transactions
            .Where(t => t.UserId == userId
                        && t.CategoryId == null
                        && !ProtectedSources.Contains(t.CategorySource))
            .OrderBy(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        if (backlog.Count == 0)
        {
            return;
        }

        // Idempotency: any transaction that already has a Pending suggestion is skipped so a second
        // run never double-queues (guard by (UserId, TransactionId, Status=Pending)).
        var backlogIds = backlog.Select(t => t.Id).ToList();
        var alreadyPending = await _db.AiSuggestions
            .Where(s => s.UserId == userId
                        && s.Status == AiSuggestionStatus.Pending
                        && s.TransactionId != null
                        && backlogIds.Contains(s.TransactionId.Value))
            .Select(s => s.TransactionId!.Value)
            .ToListAsync(ct);
        var pendingSet = alreadyPending.ToHashSet();

        if (_classifier is IBatchAiCategorizationService batchClassifier)
        {
            await CategorizeBatchedAsync(userId, backlog, pendingSet, threshold, batchClassifier, ct);
            return;
        }

        var now = DateTime.UtcNow;

        foreach (var txn in backlog)
        {
            if (pendingSet.Contains(txn.Id))
            {
                continue;
            }

            CategoryClassification decision;
            try
            {
                decision = await _classifier.ClassifyAsync(userId, txn.Id, ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "AiCategorizationJob stopped early for user {UserId} at transaction {TransactionId}: LLM provider request failed; already classified items were saved.",
                    userId,
                    txn.Id);
                break;
            }

            if (ApplyDecision(userId, txn, decision, threshold, now, pendingSet))
            {
                await _db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task CategorizeBatchedAsync(
        Guid userId,
        List<Transaction> backlog,
        HashSet<Guid> pendingSet,
        double threshold,
        IBatchAiCategorizationService batchClassifier,
        CancellationToken ct)
    {
        var eligible = backlog.Where(t => !pendingSet.Contains(t.Id)).ToList();
        if (eligible.Count == 0)
        {
            return;
        }

        // Group identical merchants/descriptions together so each chunk of UniqueGroupsPerChunk (40)
        // unique groups can classify hundreds of repeated transactions in a single LLM request.
        var groups = eligible
            .GroupBy(t => BuildGroupKey(t), StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();

        var totalChunks = (groups.Count + UniqueGroupsPerChunk - 1) / UniqueGroupsPerChunk;
        _logger.LogInformation(
            "AiCategorizationJob starting batched run for user {UserId}: {TransactionCount} transactions collapsed into {UniqueGroups} unique groups ({ChunkCount} batch(es)).",
            userId,
            eligible.Count,
            groups.Count,
            totalChunks);

        var totalApplied = 0;
        for (var chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
        {
            var chunkGroups = groups
                .Skip(chunkIndex * UniqueGroupsPerChunk)
                .Take(UniqueGroupsPerChunk);
            var chunkTxns = chunkGroups.SelectMany(g => g).ToList();
            var chunkIds = chunkTxns.Select(t => t.Id).ToList();

            IReadOnlyDictionary<Guid, CategoryClassification>? decisions = null;
            for (var attempt = 0; attempt <= MaxRateLimitRetries; attempt++)
            {
                try
                {
                    decisions = await batchClassifier.ClassifyBatchAsync(userId, chunkIds, ct);
                    break;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                {
                    _logger.LogWarning(
                        ex,
                        "AiCategorizationJob hit 429 rate limit on batch {Batch}/{Total} for user {UserId}; waiting {Seconds}s before retry {Retry}/{MaxRetries} (earlier batches already saved).",
                        chunkIndex + 1,
                        totalChunks,
                        userId,
                        (int)RateLimitBackoff.TotalSeconds,
                        attempt + 1,
                        MaxRateLimitRetries);
                    await _delay(RateLimitBackoff, ct);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "AiCategorizationJob stopped early on batch {Batch}/{Total} for user {UserId}: LLM provider request failed; {Applied} earlier transactions were saved.",
                        chunkIndex + 1,
                        totalChunks,
                        userId,
                        totalApplied);
                    return;
                }
            }

            if (decisions is null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var batchApplied = 0;
            foreach (var txn in chunkTxns)
            {
                if (!decisions.TryGetValue(txn.Id, out var decision))
                {
                    continue;
                }

                if (ApplyDecision(userId, txn, decision, threshold, now, pendingSet))
                {
                    batchApplied++;
                }
            }

            if (batchApplied > 0)
            {
                await _db.SaveChangesAsync(ct);
                totalApplied += batchApplied;
            }

            _logger.LogInformation(
                "AiCategorizationJob completed batch {Batch}/{Total} for user {UserId}: {BatchApplied}/{ChunkCount} transactions categorized/suggested ({TotalApplied} total so far).",
                chunkIndex + 1,
                totalChunks,
                userId,
                batchApplied,
                chunkTxns.Count,
                totalApplied);
        }
    }

    private bool ApplyDecision(
        Guid userId,
        Transaction txn,
        CategoryClassification decision,
        double threshold,
        DateTime now,
        HashSet<Guid> pendingSet)
    {
        // Nothing resolved (no rule/mapping, AI off/over budget, or rejected LLM answer) — leave it.
        if (decision.CategoryId is null || decision.Source == ClassificationSource.None)
        {
            return false;
        }

        if (decision.Confidence >= threshold)
        {
            // Auto-apply (AI-CAT-03): reversible, but AI must never re-touch it (now AiAuto, which
            // ProtectedSources excludes on future runs).
            txn.CategoryId = decision.CategoryId;
            txn.CategorySource = CategorySource.AiAuto;
            txn.CategoryConfidence = decision.Confidence;
            txn.CategoryModel = decision.Model;
            if (decision.MarkAsTransfer && !txn.IsTransferManualOverride)
            {
                txn.IsTransfer = true;
            }
            txn.UpdatedAt = now;
        }
        else
        {
            // Below threshold: queue a reviewable suggestion; the transaction stays "Sem categoria".
            _db.AiSuggestions.Add(new AiSuggestion
            {
                UserId = userId,
                Type = AiSuggestionType.Category,
                TransactionId = txn.Id,
                SuggestedPayload = BuildCategoryPayload(decision.CategoryId.Value),
                Confidence = decision.Confidence,
                Rationale = decision.Rationale,
                Source = MapSource(decision.Source),
                Status = AiSuggestionStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            });
            pendingSet.Add(txn.Id);
        }

        return true;
    }

    private static string BuildGroupKey(Transaction txn) =>
        string.Concat(
            NormalizeKeyPart(txn.Description),
            "|",
            NormalizeKeyPart(txn.MerchantName),
            "|",
            NormalizeKeyPart(txn.PluggyCategoryId),
            "|",
            NormalizeKeyPart(txn.PluggyCategory),
            "|",
            (int)txn.Type);

    private static string NormalizeKeyPart(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string BuildCategoryPayload(Guid categoryId) =>
        JsonSerializer.Serialize(new { categoryId });

    private static AiSuggestionSource MapSource(ClassificationSource source) => source switch
    {
        ClassificationSource.Knn => AiSuggestionSource.Embedding,
        ClassificationSource.Llm => AiSuggestionSource.Llm,
        // Rule/Mapping only reach here if a very high threshold rejected an otherwise-certain
        // deterministic hit — not an AI decision, so bucket as Statistical.
        _ => AiSuggestionSource.Statistical,
    };
}
