using System.Collections.Generic;

namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// Binds to the "Ai" configuration section (Ai__* — see .env.example / appsettings.json). Holds the
/// server-to-server provider credentials and base URLs plus the config-driven cost-rate table.
/// API keys are env-only, never persisted to the DB and never returned by the API (AI-FND-07) —
/// same handling as the Pluggy client/secret. The <see cref="Pricing"/> table is keyed by model
/// NAME so the imminent deepseek-chat/deepseek-reasoner → deepseek-v4-flash rename (deprecation
/// 2026-07-24) and any rate change are a config edit, never a code change (RESEARCH Pitfall 2).
/// </summary>
public class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>DeepSeek bearer key — env only (Ai__DeepSeekApiKey). Never stored, never returned.</summary>
    public string DeepSeekApiKey { get; set; } = string.Empty;

    /// <summary>OpenAI bearer key — env only (Ai__OpenAiApiKey). Never stored, never returned.</summary>
    public string OpenAiApiKey { get; set; } = string.Empty;

    /// <summary>Anthropic (Claude) API key — env fallback (Ai__AnthropicApiKey). Never returned.</summary>
    public string AnthropicApiKey { get; set; } = string.Empty;

    /// <summary>OpenRouter bearer key — env fallback (Ai__OpenRouterApiKey). Never returned.</summary>
    public string OpenRouterApiKey { get; set; } = string.Empty;

    public AiProviderEndpoint DeepSeek { get; set; } = new() { BaseUrl = "https://api.deepseek.com" };

    public AiProviderEndpoint OpenAi { get; set; } = new() { BaseUrl = "https://api.openai.com/v1/" };

    /// <summary>Anthropic Messages API root (the official SDK appends <c>/v1/messages</c>).</summary>
    public AiProviderEndpoint Anthropic { get; set; } = new() { BaseUrl = "https://api.anthropic.com" };

    /// <summary>OpenRouter OpenAI-compatible root — the trailing slash matters (relative request path).</summary>
    public AiProviderEndpoint OpenRouter { get; set; } = new() { BaseUrl = "https://openrouter.ai/api/v1/" };

    /// <summary>
    /// OpenAI embeddings model — config-driven (Ai:OpenAiEmbeddingModel), never a hard-coded literal
    /// in the client (same rule as the chat models). Defaults to <c>text-embedding-3-small</c> (1536
    /// dims, CONTEXT lock); the <see cref="Pricing"/> table must carry a matching rate row.
    /// </summary>
    public string OpenAiEmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Optional output dimension override for <c>POST /embeddings</c> (Ai:OpenAiEmbeddingDimensions).
    /// Useful when pointing <see cref="OpenAi"/> at an OpenAI-compatible provider such as Google AI
    /// Studio's <c>models/gemini-embedding-2</c> (default 3072 dims) so returned vectors match
    /// pgvector's <c>vector(1536)</c> column. Omitted from the wire payload when <c>null</c>.
    /// </summary>
    public int? OpenAiEmbeddingDimensions { get; set; }

    /// <summary>
    /// model name → USD-per-1M-token rates. Seeded by NAME in appsettings so rates tune without a
    /// redeploy. Unknown models estimate 0 (still recorded) rather than blocking (AI-FND-04).
    /// </summary>
    public Dictionary<string, ModelPricing> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Base URL for a single AI provider's HTTP API (the typed clients land in 15-05).</summary>
public class AiProviderEndpoint
{
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// USD-per-1M-token rates for one model. <see cref="CachedInputPerMillion"/> / <see cref="OutputPerMillion"/>
/// are optional (embeddings have no output rate); the conservative v1 estimate uses the cache-miss
/// input rate (RESEARCH §Cost Estimation).
/// </summary>
public class ModelPricing
{
    public decimal InputPerMillion { get; set; }

    public decimal? CachedInputPerMillion { get; set; }

    public decimal? OutputPerMillion { get; set; }
}
