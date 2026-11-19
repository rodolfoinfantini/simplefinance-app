using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFinance.Infrastructure.Ai.DeepSeek.Dtos;

/// <summary>
/// PRIVATE DeepSeek wire DTOs for the OpenAI-compatible <c>POST /chat/completions</c> endpoint
/// (15-RESEARCH §DeepSeek DTO shapes). These are the on-the-wire JSON shapes and MUST NOT leak past
/// Infrastructure — <see cref="DeepSeekLlmClient"/> maps them to/from the provider-neutral
/// <see cref="SimpleFinance.Application.Ai.LlmChatRequest"/> / <c>LlmResult</c> records, exactly as
/// ProviderDtos.cs does for Pluggy. DeepSeek-specific deviations from the OpenAI schema are handled
/// here: <c>reasoning_content</c> (reasoner models) and the <c>prompt_cache_hit_tokens</c> /
/// <c>prompt_cache_miss_tokens</c> usage fields. Model name always travels on
/// <see cref="DeepSeekChatRequest.Model"/> — never a constant.
/// </summary>
internal sealed record DeepSeekChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<DeepSeekMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream = false,
    [property: JsonPropertyName("tools")] IReadOnlyList<DeepSeekTool>? Tools = null,
    [property: JsonPropertyName("tool_choice")] string? ToolChoice = null,
    [property: JsonPropertyName("stream_options")] DeepSeekStreamOptions? StreamOptions = null);

internal sealed record DeepSeekStreamOptions(
    [property: JsonPropertyName("include_usage")] bool IncludeUsage);

internal sealed record DeepSeekMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<DeepSeekToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

internal sealed record DeepSeekTool(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] DeepSeekFunction Function);

internal sealed record DeepSeekFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

internal sealed record DeepSeekToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] DeepSeekToolCallFunction Function,
    [property: JsonPropertyName("extra_content")] JsonElement? ExtraContent = null);

internal sealed record DeepSeekToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

// --- response ---

internal sealed record DeepSeekChatResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<DeepSeekChoice> Choices,
    [property: JsonPropertyName("usage")] DeepSeekUsage? Usage);

internal sealed record DeepSeekChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] DeepSeekResponseMessage Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record DeepSeekResponseMessage(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<DeepSeekToolCall>? ToolCalls,
    [property: JsonPropertyName("extra_content")] JsonElement? ExtraContent = null);

internal sealed record DeepSeekUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens,
    [property: JsonPropertyName("prompt_cache_hit_tokens")] int? PromptCacheHitTokens = null,
    [property: JsonPropertyName("prompt_cache_miss_tokens")] int? PromptCacheMissTokens = null);

// --- streaming (SSE delta) ---

internal sealed record DeepSeekStreamChunk(
    [property: JsonPropertyName("choices")] IReadOnlyList<DeepSeekStreamChoice>? Choices,
    [property: JsonPropertyName("usage")] DeepSeekUsage? Usage);

internal sealed record DeepSeekStreamChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] DeepSeekStreamDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record DeepSeekStreamDelta(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent);
