using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.DeepSeek.Dtos;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.OpenAiCompatible;

/// <summary>
/// Shared <see cref="ILlmClient"/> over any OpenAI-compatible <c>POST /chat/completions</c> API
/// (DeepSeek, OpenRouter). Hand-rolled System.Text.Json, no SDK; the private wire DTOs (in
/// <c>DeepSeek/Dtos</c>, which already model the OpenAI shape plus DeepSeek's optional extras) are
/// mapped to/from the neutral Application records so no provider JSON shape leaks past Infrastructure.
/// Subclasses only pick the <see cref="Provider"/> recorded on usage rows, the streaming HttpClient
/// name and (optionally) serializer options; the typed resilient HttpClient + bearer handler come
/// from DI.
///
/// Enforces the un-bypassable invariant ordering <b>redact → budget-check → send → record</b>
/// internally so no caller can skip PII scrubbing or the monthly kill-switch:
/// <list type="number">
/// <item>every outbound message string is passed through <see cref="IPiiRedactor"/> BEFORE serialization;</item>
/// <item><see cref="IAiBudgetGuard.EnsureWithinBudgetAsync"/> is awaited BEFORE any HTTP send (throws
/// <c>AiBudgetExceededException</c> to make the caller degrade — zero HTTP when over budget);</item>
/// <item>the request is sent (non-streaming on the resilient typed client, streaming on a separate
/// relaxed-timeout client so SSE is not aborted, RESEARCH Pitfall 3);</item>
/// <item><see cref="IAiUsageRecorder.RecordAsync"/> records the returned tokens after the response.</item>
/// </list>
/// The model name always comes from <see cref="LlmChatRequest.Model"/> (config-driven via
/// UserSettings) — never a hard-coded constant. Single-user app: the owning UserId is resolved from
/// the one <c>User</c> row for budget/usage scoping.
/// </summary>
public abstract class OpenAiCompatibleLlmClient : ILlmClient
{
    private const string ChatCompletionsPath = "chat/completions";

    private readonly HttpClient _httpClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPiiRedactor _redactor;
    private readonly IAiBudgetGuard _budgetGuard;
    private readonly IAiUsageRecorder _usageRecorder;
    private readonly SimpleFinanceDbContext _db;
    private readonly ConcurrentDictionary<string, JsonElement> _toolCallExtraContent = new(StringComparer.Ordinal);

    protected OpenAiCompatibleLlmClient(
        HttpClient httpClient,
        IHttpClientFactory httpClientFactory,
        IPiiRedactor redactor,
        IAiBudgetGuard budgetGuard,
        IAiUsageRecorder usageRecorder,
        SimpleFinanceDbContext db)
    {
        _httpClient = httpClient;
        _httpClientFactory = httpClientFactory;
        _redactor = redactor;
        _budgetGuard = budgetGuard;
        _usageRecorder = usageRecorder;
        _db = db;
    }

    /// <summary>Provider recorded on every AiUsageLog row this client writes.</summary>
    protected abstract AiProvider Provider { get; }

    /// <summary>Human-readable provider name used in error messages.</summary>
    protected abstract string ProviderName { get; }

    /// <summary>Named relaxed-timeout HttpClient used for SSE streaming.</summary>
    protected abstract string StreamingClientName { get; }

    /// <summary>
    /// Serializer options for the outbound body. <c>null</c> ⇒ the System.Net.Http.Json web defaults
    /// (the original DeepSeek wire format, nulls included).
    /// </summary>
    protected virtual JsonSerializerOptions? SerializerOptions => null;

    public async Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);

        // 1. redact every outbound string, then 2. budget-check — both BEFORE any HTTP send.
        var wireRequest = BuildWireRequest(request, stream: false, includeUsage: false);
        await _budgetGuard.EnsureWithinBudgetAsync(userId, ct);

        // 3. send
        using var response = await _httpClient.PostAsJsonAsync(ChatCompletionsPath, wireRequest, SerializerOptions, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DeepSeekChatResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"{ProviderName} POST /chat/completions returned an empty response body.");

        var result = MapResponse(body);

        // 4. record usage (so the guard's next month-sum includes this call).
        await _usageRecorder.RecordAsync(
            userId,
            request.Feature,
            Provider,
            request.Model,
            result.Usage.PromptTokens,
            result.Usage.CompletionTokens,
            ct);

        return result;
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);

        // Same invariant as ChatAsync: redact (in BuildWireRequest) + budget-check before the stream opens.
        var wireRequest = BuildWireRequest(request, stream: true, includeUsage: true);
        await _budgetGuard.EnsureWithinBudgetAsync(userId, ct);

        // Separate relaxed-timeout client — the standard resilience total-request timeout aborts SSE.
        var streamingClient = _httpClientFactory.CreateClient(StreamingClientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
        {
            Content = JsonContent.Create(wireRequest, options: SerializerOptions),
        };

        using var response = await streamingClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        DeepSeekUsage? finalUsage = null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            DeepSeekStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<DeepSeekStreamChunk>(data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk is null)
            {
                continue;
            }

            if (chunk.Usage is not null)
            {
                finalUsage = chunk.Usage;
            }

            var delta = chunk.Choices is { Count: > 0 } ? chunk.Choices[0].Delta : null;
            if (delta is not null && (delta.Content is not null || delta.ReasoningContent is not null))
            {
                yield return new LlmStreamChunk(ContentDelta: delta.Content, ReasoningDelta: delta.ReasoningContent);
            }
        }

        // usage arrives on the final chunk only when stream_options.include_usage = true.
        if (finalUsage is not null)
        {
            yield return new LlmStreamChunk(Usage: new TokenUsage(finalUsage.PromptTokens, finalUsage.CompletionTokens));
            await _usageRecorder.RecordAsync(
                userId,
                request.Feature,
                Provider,
                request.Model,
                finalUsage.PromptTokens,
                finalUsage.CompletionTokens,
                ct);
        }
    }

    /// <summary>
    /// Maps the neutral request → OpenAI-compatible wire request, redacting every message content string
    /// first (belt-and-suspenders over the structural avoidance of PII — T-15-10). Model is taken
    /// verbatim from the request; never a constant.
    /// </summary>
    private DeepSeekChatRequest BuildWireRequest(LlmChatRequest request, bool stream, bool includeUsage)
    {
        var messages = request.Messages
            .Select(m => new DeepSeekMessage(
                Role: m.Role,
                Content: m.ToolCalls is { Count: > 0 } && string.IsNullOrEmpty(m.Content)
                    ? null
                    : _redactor.Redact(m.Content),
                ToolCalls: m.ToolCalls?.Select(tc => new DeepSeekToolCall(
                    Id: tc.Id,
                    Type: "function",
                    Function: new DeepSeekToolCallFunction(tc.Name, tc.ArgumentsJson),
                    ExtraContent: _toolCallExtraContent.TryGetValue(tc.Id, out var extra) ? extra : null)).ToList(),
                ToolCallId: m.ToolCallId))
            .ToList();

        var tools = request.Tools?
            .Select(t => new DeepSeekTool("function", new DeepSeekFunction(t.Name, t.Description, t.Parameters)))
            .ToList();

        return new DeepSeekChatRequest(
            Model: request.Model,
            Messages: messages,
            Stream: stream,
            Tools: tools,
            ToolChoice: request.ToolChoice,
            StreamOptions: includeUsage ? new DeepSeekStreamOptions(IncludeUsage: true) : null);
    }

    private LlmResult MapResponse(DeepSeekChatResponse response)
    {
        var message = response.Choices is { Count: > 0 } ? response.Choices[0].Message : null;
        if (message?.ToolCalls is { Count: > 0 })
        {
            for (var i = 0; i < message.ToolCalls.Count; i++)
            {
                var tc = message.ToolCalls[i];
                var extra = tc.ExtraContent is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }
                    ? tc.ExtraContent.Value
                    : i == 0 && message.ExtraContent is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }
                        ? message.ExtraContent.Value
                        : (JsonElement?)null;

                if (extra is not null)
                {
                    _toolCallExtraContent[tc.Id] = extra.Value.Clone();
                }
            }
        }

        var toolCalls = message?.ToolCalls?
            .Select(tc => new LlmToolCall(tc.Id, tc.Function.Name, tc.Function.Arguments))
            .ToList() ?? [];

        var usage = response.Usage is null
            ? new TokenUsage(0, 0)
            : new TokenUsage(response.Usage.PromptTokens, response.Usage.CompletionTokens);

        return new LlmResult(
            Content: message?.Content ?? string.Empty,
            ReasoningContent: message?.ReasoningContent,
            ToolCalls: toolCalls,
            Usage: usage);
    }

    /// <summary>
    /// Single-user app: resolve the one owning UserId for budget/usage scoping (mirrors the jobs'
    /// user-resolution — there is exactly one <c>User</c> row). Deterministic (oldest-first).
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
