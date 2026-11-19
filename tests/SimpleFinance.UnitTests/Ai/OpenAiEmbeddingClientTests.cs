using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.OpenAi;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// AI-FND-02 (15-05): the OpenAI embeddings client. Proven with a fake <see cref="HttpMessageHandler"/>
/// that echoes 1536-length vectors and can return <c>data[]</c> out of index order — so the tests
/// prove: input-order-aligned 1536-dim results, sort-by-<c>index</c> mapping (T-15-13), single-embed
/// dedup of identical normalized text, &gt;100 chunking, PII scrubbed off the wire, budget-exceeded ⇒
/// zero HTTP, and usage recorded after send. No key/network required.
/// </summary>
public class OpenAiEmbeddingClientTests
{
    private const int Dim = 1536;

    /// <summary>Fake handler: parses each request's input array, returns one 1536-vec per input whose
    /// first element encodes the within-request index; optionally returns data in reversed order.</summary>
    private sealed class EchoHandler(bool reverse = false) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? LastRequestBody { get; private set; }

        public List<int> RequestInputCounts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var bodyText = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastRequestBody = bodyText;

            using var doc = JsonDocument.Parse(bodyText);
            var input = doc.RootElement.GetProperty("input");
            var n = input.GetArrayLength();
            RequestInputCounts.Add(n);

            var indices = Enumerable.Range(0, n).ToList();
            if (reverse)
            {
                indices.Reverse();
            }

            var sb = new StringBuilder();
            sb.Append("{\"data\":[");
            for (var pos = 0; pos < indices.Count; pos++)
            {
                var idx = indices[pos];
                if (pos > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"index\":").Append(idx).Append(",\"embedding\":[");
                for (var d = 0; d < Dim; d++)
                {
                    if (d > 0)
                    {
                        sb.Append(',');
                    }

                    // element 0 encodes the index so tests can assert correct index-ordered mapping.
                    sb.Append(d == 0 ? idx.ToString() : "0");
                }

                sb.Append("]}");
            }

            sb.Append("],\"usage\":{\"prompt_tokens\":").Append(n).Append(",\"total_tokens\":").Append(n).Append("}}");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class PassThroughBudgetGuard : IAiBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingBudgetGuard : IAiBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid userId, CancellationToken ct)
            => throw new AiBudgetExceededException(userId, 5m, 3m);
    }

    private sealed class RecordingUsageRecorder : IAiUsageRecorder
    {
        public int CallCount { get; private set; }

        public string? LastModel { get; private set; }

        public AiProvider LastProvider { get; private set; }

        public AiFeature LastFeature { get; private set; }

        public Task RecordAsync(Guid userId, AiFeature feature, AiProvider provider, string model, int promptTokens, int completionTokens, CancellationToken ct)
        {
            CallCount++;
            LastModel = model;
            LastProvider = provider;
            LastFeature = feature;
            return Task.CompletedTask;
        }
    }

    private static SimpleFinanceDbContext CreateDbWithUser()
    {
        var db = new SimpleFinanceDbContext(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        var now = DateTime.UtcNow;
        db.Users.Add(new User { Id = Guid.NewGuid(), Name = "Owner", Email = "owner@example.com", PasswordHash = "x", CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();
        return db;
    }

    private static IOptions<AiOptions> Options(int? dimensions = null, string model = "text-embedding-3-small") =>
        Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            OpenAiApiKey = "test-key",
            OpenAi = new AiProviderEndpoint { BaseUrl = "https://api.openai.com/v1/" },
            OpenAiEmbeddingModel = model,
            OpenAiEmbeddingDimensions = dimensions,
        });

    private static OpenAiEmbeddingClient CreateClient(
        EchoHandler handler,
        SimpleFinanceDbContext db,
        IAiBudgetGuard guard,
        IAiUsageRecorder recorder,
        int? dimensions = null,
        string model = "text-embedding-3-small")
    {
        var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        return new OpenAiEmbeddingClient(httpClient, Options(dimensions, model), new PiiRedactor(), guard, recorder, db);
    }

    [Fact]
    public async Task EmbedAsync_Returns1536DimVectorPerInput_AlignedToInputOrder()
    {
        using var handler = new EchoHandler();
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        var result = await client.EmbedAsync(["alpha", "beta", "gamma"], CancellationToken.None);

        Assert.Equal(3, result.Length);
        Assert.All(result, v => Assert.Equal(Dim, v.Length));
    }

    [Fact]
    public async Task EmbedAsync_SortsResponseDataByIndex_NotArrayPosition()
    {
        using var handler = new EchoHandler(reverse: true);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        var result = await client.EmbedAsync(["a", "b", "c", "d"], CancellationToken.None);

        // element 0 of each vector encodes the server-declared index; after sorting by index the
        // vector at output position i must carry index i even though the server returned them reversed.
        for (var i = 0; i < result.Length; i++)
        {
            Assert.Equal(i, (int)result[i][0]);
        }
    }

    [Fact]
    public async Task EmbedAsync_DeduplicatesIdenticalNormalizedText_EmbedsOnce_FansBackToEachPosition()
    {
        using var handler = new EchoHandler();
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        // "A" appears at positions 0 and 2 (with whitespace differences that normalize away).
        var result = await client.EmbedAsync(["Mercado  Pago", "Uber", "Mercado Pago"], CancellationToken.None);

        Assert.Equal(3, result.Length);
        Assert.Equal(2, handler.RequestInputCounts[0]); // only the 2 unique texts were sent
        Assert.Equal(result[0], result[2]); // duplicate fanned back to both positions
    }

    [Fact]
    public async Task EmbedAsync_BatchesLargerThan100_AreChunked()
    {
        using var handler = new EchoHandler();
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), recorder);

        var inputs = Enumerable.Range(0, 150).Select(i => $"text-{i}").ToList();
        var result = await client.EmbedAsync(inputs, CancellationToken.None);

        Assert.Equal(150, result.Length);
        Assert.Equal(2, handler.CallCount); // 100 + 50
        Assert.All(handler.RequestInputCounts, c => Assert.True(c <= 100));
        Assert.All(result, v => Assert.Equal(Dim, v.Length));
        Assert.Equal(2, recorder.CallCount); // one usage row per HTTP call
    }

    [Fact]
    public async Task EmbedAsync_RedactsCpf_SoItNeverReachesTheWireBody()
    {
        using var handler = new EchoHandler();
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        await client.EmbedAsync(["Transferência CPF 123.456.789-09"], CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        Assert.DoesNotContain("123.456.789", handler.LastRequestBody);
        Assert.Contains("REDACTED", handler.LastRequestBody);
    }

    [Fact]
    public async Task EmbedAsync_WhenBudgetGuardThrows_MakesZeroHttpCalls()
    {
        using var handler = new EchoHandler();
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new ThrowingBudgetGuard(), recorder);

        await Assert.ThrowsAsync<AiBudgetExceededException>(
            () => client.EmbedAsync(["anything"], CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
        Assert.Equal(0, recorder.CallCount);
    }

    [Fact]
    public async Task EmbedAsync_RecordsUsage_WithConfiguredEmbeddingModel()
    {
        using var handler = new EchoHandler();
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), recorder);

        await client.EmbedAsync(["alpha"], CancellationToken.None);

        Assert.Equal(1, recorder.CallCount);
        Assert.Equal("text-embedding-3-small", recorder.LastModel);
        Assert.Equal(AiProvider.OpenAi, recorder.LastProvider);
        Assert.Equal(AiFeature.Embedding, recorder.LastFeature);
    }

    [Fact]
    public void HashNormalized_IsStable_AndCollapsesWhitespace()
    {
        Assert.Equal(OpenAiEmbeddingClient.HashNormalized("Mercado  Pago"), OpenAiEmbeddingClient.HashNormalized("Mercado Pago"));
        Assert.Equal(OpenAiEmbeddingClient.HashNormalized(" x "), OpenAiEmbeddingClient.HashNormalized("x"));
        Assert.NotEqual(OpenAiEmbeddingClient.HashNormalized("a"), OpenAiEmbeddingClient.HashNormalized("b"));
    }

    [Fact]
    public async Task EmbedAsync_OmitsDimensionsWhenNull_AndSendsDimensionsWhenConfigured()
    {
        using var handler = new EchoHandler();
        await using var db = CreateDbWithUser();

        var defaultClient = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());
        await defaultClient.EmbedAsync(["alpha"], CancellationToken.None);
        using (var defaultBody = JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.False(defaultBody.RootElement.TryGetProperty("dimensions", out _));
        }

        var geminiClient = CreateClient(
            handler,
            db,
            new PassThroughBudgetGuard(),
            new RecordingUsageRecorder(),
            dimensions: 1536,
            model: "models/gemini-embedding-2");
        await geminiClient.EmbedAsync(["alpha"], CancellationToken.None);
        using var configuredBody = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("models/gemini-embedding-2", configuredBody.RootElement.GetProperty("model").GetString());
        Assert.Equal(1536, configuredBody.RootElement.GetProperty("dimensions").GetInt32());
    }
}
