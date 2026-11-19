namespace SimpleFinance.Application.Categorization;

/// <summary>
/// AI-CAT-02/03: the PURE, provider-neutral result of the categorization cascade
/// (<c>AiCategorizationService</c> in Infrastructure). It is a read-only DECISION — the cascade
/// never writes to the transaction or creates an <c>AiSuggestion</c>. 16-04's job consumes this
/// record and decides, per <see cref="Confidence"/> vs the user's auto-apply threshold, whether to
/// auto-apply the category (<c>CategorySource.AiAuto</c>) or persist a reviewable suggestion —
/// keeping the never-overwrite rule enforceable in exactly one place.
/// </summary>
/// <param name="CategoryId">
/// The resolved category, or <c>null</c> when nothing confident was found (<see cref="Source"/> is
/// then <see cref="ClassificationSource.None"/>, or an LLM answer that failed validation).
/// </param>
/// <param name="Confidence">
/// 0..1. Deterministic hits are certain (Rule = 1.0, Mapping ≈ 0.95); kNN/LLM carry a computed or
/// model-reported confidence. 16-04 branches on this against <c>UserSettings.AiAutoApplyThreshold</c>.
/// </param>
/// <param name="Model">
/// The model that produced an AI decision (the embedding model for kNN, the chat model for the LLM),
/// stored on <c>Transaction.CategoryModel</c> when applied. <c>null</c> for deterministic sources.
/// </param>
/// <param name="Source">Which cascade level produced this decision.</param>
/// <param name="Rationale">Short human-readable justification — only the LLM level populates it.</param>
public sealed record CategoryClassification(
    Guid? CategoryId,
    double Confidence,
    string? Model,
    ClassificationSource Source,
    string? Rationale,
    bool MarkAsTransfer = false);

/// <summary>Which level of the 4-step cascade produced a <see cref="CategoryClassification"/>.</summary>
public enum ClassificationSource
{
    /// <summary>Matched by one of the user's own <c>CategoryRule</c>s (level 1) — certain.</summary>
    Rule,

    /// <summary>Resolved via the Pluggy→Category <c>CategoryMapping</c> (level 2) — near-certain.</summary>
    Mapping,

    /// <summary>Assigned by the pgvector kNN vote over the user's trusted labels (level 3).</summary>
    Knn,

    /// <summary>Assigned by the <c>deepseek-chat</c> strict-JSON fallback (level 4).</summary>
    Llm,

    /// <summary>Nothing resolved (no rule/mapping, and AI off/over-budget or no confident signal).</summary>
    None
}
