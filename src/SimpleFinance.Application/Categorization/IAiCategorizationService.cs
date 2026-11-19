namespace SimpleFinance.Application.Categorization;

/// <summary>
/// AI-CAT-02/03: the read-only categorization cascade. Classifies ONE transaction in order —
/// user rules → Pluggy mapping → kNN vote (pgvector) → <c>deepseek-chat</c> strict-JSON fallback —
/// stopping at the first confident hit, and returns a pure <see cref="CategoryClassification"/>
/// decision. It writes NOTHING: it never sets <c>Transaction.CategoryId</c> and never creates an
/// <c>AiSuggestion</c>. 16-04's job owns applying the decision (auto-apply vs suggestion) and the
/// never-overwrite rule, so this seam stays trivially unit-testable per cascade level.
/// </summary>
public interface IAiCategorizationService
{
    /// <summary>
    /// Classifies the transaction identified by <paramref name="transactionId"/> for
    /// <paramref name="userId"/>. Always returns a decision — <see cref="ClassificationSource.None"/>
    /// with a null category when nothing resolves (including when AI is disabled or over budget).
    /// Never throws for an expected degrade (budget cap / AI off).
    /// </summary>
    Task<CategoryClassification> ClassifyAsync(Guid userId, Guid transactionId, CancellationToken ct);
}

/// <summary>
/// Optional batch-classification capability implemented by <c>AiCategorizationService</c>.
/// Processes a page/chunk of transactions at once: runs deterministic rules/mappings per row,
/// deduplicates remaining rows by normalized redacted text, runs batched kNN embeddings only when
/// trusted labels exist, and classifies unresolved unique merchants in batched LLM prompts.
/// </summary>
public interface IBatchAiCategorizationService : IAiCategorizationService
{
    /// <summary>
    /// Classifies a batch of transactions for <paramref name="userId"/>. Returns a map of
    /// <c>transactionId → CategoryClassification</c> for every transaction processed in this batch.
    /// May throw <see cref="System.Net.Http.HttpRequestException"/> (e.g. 429 rate-limit) so the
    /// caller can back off and retry the batch without losing earlier batches.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CategoryClassification>> ClassifyBatchAsync(
        Guid userId,
        IReadOnlyList<Guid> transactionIds,
        CancellationToken ct);
}
