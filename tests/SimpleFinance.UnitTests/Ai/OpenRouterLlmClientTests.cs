using System.Text.Json;
using SimpleFinance.Application.Ai;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.OpenRouter;
using static SimpleFinance.UnitTests.Ai.ProviderClientTestSupport;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// OpenRouter chat client (OpenAI-compatible, shares the DeepSeek base). Fake handler, no network:
/// redact → budget → send → record, usage recorded as OpenRouter, slug model sent verbatim, tool calls
/// mapped back, nulls omitted from the body, no attribution headers, streaming via the named client.
/// </summary>
public class OpenRouterLlmClientTests
{
    private const string CannedResponseJson = """
        {
          "choices": [
            { "index": 0,
              "message": { "content": "Resposta", "tool_calls": [
                { "id": "call_1", "type": "function", "function": { "name": "get_balance", "arguments": "{\"month\":\"2026-10\"}" } }
              ] },
              "finish_reason": "tool_calls" }
          ],
          "usage": { "prompt_tokens": 30, "completion_tokens": 9, "total_tokens": 39 }
        }
        """;

    private static readonly Uri BaseAddress = new("https://openrouter.ai/api/v1/");

    private static OpenRouterLlmClient CreateClient(
        CapturingHandler handler,
        Infrastructure.Persistence.SimpleFinanceDbContext db,
        Application.Ai.Usage.IAiBudgetGuard guard,
        RecordingUsageRecorder recorder,
        SingleClientFactory? factory = null)
    {
        var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = BaseAddress };
        return new OpenRouterLlmClient(httpClient, factory ?? new SingleClientFactory(handler, BaseAddress), new PiiRedactor(), guard, recorder, db);
    }

    private static LlmChatRequest Request(string content, string model = "openai/gpt-5") =>
        new(model, [new LlmMessage("user", content)], AiFeature.Assistant);

    [Fact]
    public async Task ChatAsync_PostsToChatCompletions_MapsResult_AndRecordsOpenRouterUsage()
    {
        using var handler = new CapturingHandler(CannedResponseJson);
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), recorder);

        var result = await client.ChatAsync(Request("Olá"), CancellationToken.None);

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Resposta", result.Content);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("get_balance", call.Name);
        Assert.Equal("{\"month\":\"2026-10\"}", call.ArgumentsJson);
        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(AiProvider.OpenRouter, recorder.LastProvider);
        Assert.Equal("openai/gpt-5", recorder.LastModel);
        Assert.Equal(30, recorder.LastPromptTokens);
        Assert.Equal(9, recorder.LastCompletionTokens);
    }

    [Fact]
    public async Task ChatAsync_SendsSlugVerbatim_OmitsNulls_AndNoAttributionHeaders()
    {
        using var handler = new CapturingHandler(CannedResponseJson);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        await client.ChatAsync(Request("Olá", model: "anthropic/claude-sonnet-5-5"), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("anthropic/claude-sonnet-5-5", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("tool_choice", out _));
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.False(body.RootElement.TryGetProperty("stream_options", out _));
        Assert.False(handler.LastRequest!.Headers.Contains("HTTP-Referer"));
        Assert.False(handler.LastRequest.Headers.Contains("X-Title"));
    }

    [Fact]
    public async Task ChatAsync_RedactsPii_BeforeSerialization()
    {
        using var handler = new CapturingHandler(CannedResponseJson);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        await client.ChatAsync(Request("Pagamento para CPF 123.456.789-09 confirmado"), CancellationToken.None);

        Assert.DoesNotContain("123.456.789", handler.LastRequestBody);
        Assert.Contains("REDACTED", handler.LastRequestBody);
    }

    [Fact]
    public async Task ChatAsync_WhenBudgetGuardThrows_MakesZeroHttpCalls()
    {
        using var handler = new CapturingHandler(CannedResponseJson);
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new ThrowingBudgetGuard(), recorder);

        await Assert.ThrowsAsync<Application.Ai.Usage.AiBudgetExceededException>(
            () => client.ChatAsync(Request("x"), CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
        Assert.Equal(0, recorder.CallCount);
    }

    [Fact]
    public async Task ChatStreamAsync_UsesNamedStreamingClient_YieldsDeltas_AndRecordsFinalUsage()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"content":"Ol"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"content":"á"},"finish_reason":"stop"}]}

            data: {"choices":[],"usage":{"prompt_tokens":11,"completion_tokens":2,"total_tokens":13}}

            data: [DONE]

            """;
        using var handler = new CapturingHandler(sse, "text/event-stream");
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var factory = new SingleClientFactory(handler, BaseAddress);
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), recorder, factory);

        var text = "";
        TokenUsage? usage = null;
        await foreach (var chunk in client.ChatStreamAsync(Request("oi"), CancellationToken.None))
        {
            text += chunk.ContentDelta;
            usage ??= chunk.Usage;
        }

        Assert.Equal([OpenRouterLlmClient.StreamingHttpClientName], factory.RequestedNames);
        Assert.Equal("Olá", text);
        Assert.Equal(new TokenUsage(11, 2), usage);
        Assert.Equal(AiProvider.OpenRouter, recorder.LastProvider);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task ChatAsync_RoundTripsToolCallExtraContent_AndOmitsEmptyAssistantContent()
    {
        const string toolCallWithSignatureJson = """
            {
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_sig_1",
                        "type": "function",
                        "function": { "name": "sum_by_category", "arguments": "{\"period\":\"this_month\",\"type\":\"expense\"}" },
                        "extra_content": {
                          "google": {
                            "thought_signature": "sig-abc-123"
                          }
                        }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": { "prompt_tokens": 20, "completion_tokens": 5, "total_tokens": 25 }
            }
            """;

        using var handler = new CapturingHandler(toolCallWithSignatureJson);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        var firstTurn = await client.ChatAsync(Request("Quanto gastei?", model: "models/gemini-3.8-flash"), CancellationToken.None);

        var followUp = new LlmChatRequest(
            Model: "models/gemini-3.8-flash",
            Messages:
            [
                new LlmMessage("user", "Quanto gastei?"),
                new LlmMessage("assistant", firstTurn.Content, ToolCalls: firstTurn.ToolCalls),
                new LlmMessage("tool", """{"total":100}""", ToolCallId: "call_sig_1"),
            ],
            Feature: AiFeature.Assistant);

        await client.ChatAsync(followUp, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var assistantMsg = body.RootElement.GetProperty("messages")[1];
        Assert.False(assistantMsg.TryGetProperty("content", out _));
        var echoedToolCall = assistantMsg.GetProperty("tool_calls")[0];
        Assert.Equal(
            "sig-abc-123",
            echoedToolCall.GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString());
    }
}
