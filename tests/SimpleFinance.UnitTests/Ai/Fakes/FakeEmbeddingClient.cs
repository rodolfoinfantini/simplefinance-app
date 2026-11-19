using System.Security.Cryptography;
using System.Text;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;

namespace SimpleFinance.UnitTests.Ai.Fakes;

/// <summary>
/// Key-free <see cref="IEmbeddingClient"/> substitute injected by every later AI test. Returns a
/// deterministic 1536-length vector per input (seeded by the SHA-256 hash of the text, so identical
/// texts embed identically) and records how many texts it has been asked to embed so dedup tests
/// can assert. Reads no API key and makes no network call.
/// </summary>
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    /// <summary>Dimensionality of the returned vectors — matches text-embedding-3-small (1536).</summary>
    public const int Dimensions = 1536;

    /// <summary>Total number of texts passed to <see cref="EmbedAsync"/> across all calls.</summary>
    public int TotalTextsEmbedded { get; private set; }

    /// <summary>Number of times <see cref="EmbedAsync"/> was invoked.</summary>
    public int CallCount { get; private set; }

    /// <summary>Every text this fake was asked to embed, in call/order — lets the embedding-job tests
    /// assert that a cache-hit text was never re-sent (16-02).</summary>
    public List<string> ReceivedTexts { get; } = [];

    /// <summary>When true, <see cref="EmbedAsync"/> throws <see cref="AiBudgetExceededException"/> on
    /// invocation instead of returning vectors — mirrors the real client's budget-guard degrade so the
    /// job's graceful-stop behavior can be exercised without a real budget guard.</summary>
    public bool ThrowBudgetExceeded { get; set; }

    public bool ThrowHttpRequestException { get; set; }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        CallCount++;
        TotalTextsEmbedded += texts.Count;
        ReceivedTexts.AddRange(texts);

        if (ThrowBudgetExceeded)
        {
            throw new AiBudgetExceededException(Guid.Empty, 5m, 3m);
        }

        if (ThrowHttpRequestException)
        {
            throw new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized);
        }

        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            vectors[i] = Deterministic(texts[i]);
        }

        return Task.FromResult(vectors);
    }

    /// <summary>Produces a stable 1536-length vector from a text's SHA-256 hash used as a PRNG seed.</summary>
    private static float[] Deterministic(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        var seed = BitConverter.ToInt32(hash, 0);
        var rng = new Random(seed);

        var vector = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
        {
            vector[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        return vector;
    }
}
