using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleFinance.Application.Categorization;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
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
    /// <summary>Max transactions classified per user per run — bounds cost/runtime (T-16-10).</summary>
    private const int PageSize = 500;

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

    public AiCategorizationJob(
        SimpleFinanceDbContext db,
        IAiCategorizationService classifier,
        ILogger<AiCategorizationJob> logger)
    {
        _db = db;
        _classifier = classifier;
        _logger = logger;
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

        // Backlog: genuinely uncategorized, non-protected rows. The global soft-delete query filter
        // already excludes DeletedAt != null. Excluding ProtectedSources enforces never-overwrite
        // BEFORE any classification call (AI-CAT-04) — a Rule/UserOverride/AiSuggested/AiAuto row is
        // never even read into the cascade.
        var backlog = await _db.Transactions
            .Where(t => t.UserId == userId
                        && t.CategoryId == null
                        && !ProtectedSources.Contains(t.CategorySource))
            .OrderBy(t => t.CreatedAt)
            .Take(PageSize)
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

            // Nothing resolved (no rule/mapping, AI off/over budget, or rejected LLM answer) — leave it.
            if (decision.CategoryId is null || decision.Source == ClassificationSource.None)
            {
                continue;
            }

            if (decision.Confidence >= threshold)
            {
                // Auto-apply (AI-CAT-03): reversible, but AI must never re-touch it (now AiAuto, which
                // ProtectedSources excludes on future runs).
                txn.CategoryId = decision.CategoryId;
                txn.CategorySource = CategorySource.AiAuto;
                txn.CategoryConfidence = decision.Confidence;
                txn.CategoryModel = decision.Model;
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

            await _db.SaveChangesAsync(ct);
        }
    }

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
