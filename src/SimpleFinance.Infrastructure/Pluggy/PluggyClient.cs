using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SimpleFinance.Infrastructure.Pluggy.Dtos;

namespace SimpleFinance.Infrastructure.Pluggy;

/// <summary>
/// Custom HttpClient wrapper for Pluggy's REST API (02-CONTEXT.md's locked decision — hand-rolled,
/// not the official Pluggy.SDK, to keep System.Text.Json throughout and avoid leaking Pluggy DTOs
/// past Infrastructure). Owns /connect_token, /items (GET/PATCH/DELETE), /items/{id}/mfa, and the
/// one-time global webhook registration. Auth (/auth) itself is owned by PluggyApiKeyProvider —
/// PluggyClient only consumes the cached key via GetApiKeyAsync, never calls /auth directly, to
/// avoid a circular dependency between the two.
///
/// Registered as a typed HttpClient (PluggyServiceCollectionExtensions) with
/// AddStandardResilienceHandler for retry/backoff/circuit-breaking on transient failures — do not
/// hand-roll Polly policies here (02-RESEARCH.md's Don't-Hand-Roll table).
/// </summary>
public sealed class PluggyClient
{
    private const string ApiKeyHeaderName = "X-API-KEY";

    // GET /v2/transactions -- the cursor-based replacement for the deprecated v1 GET /transactions
    // (removal 2026-12-31 per 02-RESEARCH.md's Pitfall 14). Never build against v1.
    private const string TransactionsPath = "/v2/transactions";

    private readonly HttpClient _httpClient;
    private readonly PluggyApiKeyProvider _apiKeyProvider;
    private readonly PluggyOptions _options;

    public PluggyClient(HttpClient httpClient, PluggyApiKeyProvider apiKeyProvider, IOptions<PluggyOptions> options)
    {
        _httpClient = httpClient;
        _apiKeyProvider = apiKeyProvider;
        _options = options.Value;
    }

    /// <summary>
    /// PLG-02: mints a FRESH Connect Token on every call — never cached/reused (30min TTL,
    /// narrow scope per 02-RESEARCH.md). Pass <paramref name="existingItemId"/> to open the
    /// widget in update/reconnect mode (PLG-05).
    /// </summary>
    public async Task<string> CreateConnectTokenAsync(string? existingItemId, CancellationToken ct)
    {
        var request = new PluggyConnectTokenRequest(
            ItemId: existingItemId,
            ClientUserId: null,
            OAuthRedirectUrl: null,
            AvoidDuplicates: true);

        var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/connect_token") { Content = JsonContent.Create(request) },
            ct);

        var body = await response.Content.ReadFromJsonAsync<PluggyConnectTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Pluggy POST /connect_token returned an empty response body.");

        return body.AccessToken;
    }

    /// <summary>GET /items/{id} — PLG-04's raw status source, mapped by ItemStatusMapper.</summary>
    public async Task<PluggyItemResponse> GetItemAsync(string itemId, CancellationToken ct)
    {
        var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/items/{Uri.EscapeDataString(itemId)}"),
            ct);

        return await response.Content.ReadFromJsonAsync<PluggyItemResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"Pluggy GET /items/{itemId} returned an empty response body.");
    }

    /// <summary>
    /// PATCH /items/{id} — used both for plain re-sync triggers (parameters null) and credential
    /// updates on reconnect (parameters populated, PLG-05).
    /// </summary>
    public async Task PatchItemAsync(string itemId, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
    {
        var request = new PluggyItemPatchRequest(
            Parameters: parameters,
            ClientUserId: null,
            WebhookUrl: null,
            Products: null);

        await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, $"/items/{Uri.EscapeDataString(itemId)}") { Content = JsonContent.Create(request) },
            ct);
    }

    /// <summary>Maps to PATCH /items/{id} with no credential update — triggers a plain re-sync.</summary>
    public Task TriggerItemUpdateAsync(string itemId, CancellationToken ct) => PatchItemAsync(itemId, parameters: null, ct);

    /// <summary>PLG-06: DELETE /items/{id}.</summary>
    public async Task DeleteItemAsync(string itemId, CancellationToken ct)
    {
        await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"/items/{Uri.EscapeDataString(itemId)}"),
            ct);
    }

    /// <summary>
    /// PLG-05: POST /items/{id}/mfa. Built for API-path completeness/testability even though the
    /// primary production UX is widget update-mode (02-RESEARCH.md's MFA Challenge/Response Flow
    /// section) — the request body is keyed by the challenge parameter's own `name` field.
    /// </summary>
    public async Task SubmitMfaAsync(string itemId, string parameterName, string parameterValue, CancellationToken ct)
    {
        var body = new Dictionary<string, string> { [parameterName] = parameterValue };

        await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"/items/{Uri.EscapeDataString(itemId)}/mfa") { Content = JsonContent.Create(body) },
            ct);
    }

    /// <summary>
    /// SYNC-01: registers ONE global webhook subscription (event: "all"), never per-Connect-Token
    /// (Pitfall 15's duplicate-subscription trap). Best-effort idempotency: attempts a GET
    /// /webhooks existence check first; on any failure/uncertainty in that check, falls back to
    /// POST /webhooks directly and treats a Conflict-shaped response as success rather than
    /// throwing (02-RESEARCH.md Assumption A2/A3 — GET /webhooks list-and-check semantics were
    /// not independently re-verified).
    /// </summary>
    public async Task EnsureWebhookRegisteredAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookPublicUrl))
        {
            // No public URL in local dev (02-CONTEXT.md's Specifics) — code-complete, no-op
            // until a real deploy URL is configured.
            return;
        }

        if (await IsWebhookAlreadyRegisteredAsync(ct))
        {
            return;
        }

        var body = new
        {
            url = _options.WebhookPublicUrl,
            @event = "all",
            headers = new Dictionary<string, string> { [_options.WebhookSecretHeaderName] = _options.WebhookSecretValue ?? string.Empty },
        };

        var request = () => new HttpRequestMessage(HttpMethod.Post, "/webhooks") { Content = JsonContent.Create(body) };
        var apiKey = await _apiKeyProvider.GetApiKeyAsync(ct);
        using var message = request();
        message.Headers.Add(ApiKeyHeaderName, apiKey);

        var response = await _httpClient.SendAsync(message, ct);

        // A prior standing subscription for this exact URL responding as a conflict is treated
        // as success (idempotent registration), not a failure — the subscription already exists.
        if (response.StatusCode is HttpStatusCode.Conflict)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<bool> IsWebhookAlreadyRegisteredAsync(CancellationToken ct)
    {
        try
        {
            var apiKey = await _apiKeyProvider.GetApiKeyAsync(ct);
            using var message = new HttpRequestMessage(HttpMethod.Get, "/webhooks");
            message.Headers.Add(ApiKeyHeaderName, apiKey);

            var response = await _httpClient.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("url", out var urlProperty)
                    && string.Equals(urlProperty.GetString(), _options.WebhookPublicUrl, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            // Best-effort only (02-RESEARCH.md Assumption A2) — any failure here falls through
            // to attempting POST /webhooks, which itself treats a Conflict as success.
            return false;
        }
    }

    /// <summary>GET /accounts?itemId= -- returns an empty list (never null/exception) on zero results.</summary>
    public async Task<IReadOnlyList<PluggyAccountDto>> GetAccountsAsync(string itemId, CancellationToken ct)
    {
        var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/accounts?itemId={Uri.EscapeDataString(itemId)}"),
            ct);

        var page = await response.Content.ReadFromJsonAsync<PluggyListResponseDto<PluggyAccountDto>>(cancellationToken: ct);
        return page?.Results ?? [];
    }

    /// <summary>
    /// GET /v2/transactions?accountId= -- follows the cursor's `next` field verbatim until it is
    /// null, accumulating every page's results (02-RESEARCH.md Pattern 1). `next` carries only the
    /// query-string portion (e.g. "?accountId=X&amp;after=cursor123"), so it is combined with the
    /// fixed <see cref="TransactionsPath"/> here rather than requested as a bare relative URI --
    /// resolving a query-only relative reference directly against a pathless HttpClient.BaseAddress
    /// would silently drop the "/v2/transactions" path segment per RFC 3986 relative-resolution
    /// rules. Never hand-constructs the `after` cursor value itself.
    /// </summary>
    public async Task<IReadOnlyList<PluggyTransactionDto>> GetTransactionsAsync(string accountId, CancellationToken ct)
    {
        var results = new List<PluggyTransactionDto>();
        string? requestUri = $"{TransactionsPath}?accountId={Uri.EscapeDataString(accountId)}";

        while (requestUri is not null)
        {
            var currentUri = requestUri;
            var response = await SendAuthenticatedAsync(
                () => new HttpRequestMessage(HttpMethod.Get, currentUri),
                ct);

            var page = await response.Content.ReadFromJsonAsync<PluggyTransactionsPageDto>(cancellationToken: ct)
                ?? throw new InvalidOperationException($"Pluggy GET {currentUri} returned an empty response body.");

            results.AddRange(page.Results);
            requestUri = page.Next is null ? null : $"{TransactionsPath}{page.Next}";
        }

        return results;
    }

    /// <summary>GET /investments?itemId= -- follows Pluggy page-based pagination across all pages, returning an empty list on zero results.</summary>
    public async Task<IReadOnlyList<PluggyInvestmentDto>> GetInvestmentsAsync(string itemId, CancellationToken ct)
    {
        var results = new List<PluggyInvestmentDto>();
        var escapedItemId = Uri.EscapeDataString(itemId);
        var pageNumber = 1;

        while (true)
        {
            var requestUri = pageNumber == 1
                ? $"/investments?itemId={escapedItemId}"
                : $"/investments?itemId={escapedItemId}&page={pageNumber}";

            var response = await SendAuthenticatedAsync(
                () => new HttpRequestMessage(HttpMethod.Get, requestUri),
                ct);

            var page = await response.Content.ReadFromJsonAsync<PluggyListResponseDto<PluggyInvestmentDto>>(cancellationToken: ct);
            if (page?.Results is { Count: > 0 })
            {
                results.AddRange(page.Results);
            }

            if (page?.TotalPages is null || pageNumber >= page.TotalPages.Value)
            {
                break;
            }

            pageNumber++;
        }

        return results;
    }

    /// <summary>GET /bills?accountId= -- fetches credit card bills (most recent first), returning an empty list on zero results.</summary>
    public async Task<IReadOnlyList<PluggyBillDto>> GetBillsAsync(string accountId, CancellationToken ct)
    {
        var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/bills?accountId={Uri.EscapeDataString(accountId)}"),
            ct);

        var page = await response.Content.ReadFromJsonAsync<PluggyListResponseDto<PluggyBillDto>>(cancellationToken: ct);
        return page?.Results ?? [];
    }

    /// <summary>
    /// Shared envelope for /accounts and /investments list responses (results-wrapped, with optional
    /// page/totalPages metadata for page-based pagination on /investments).
    /// </summary>
    private sealed record PluggyListResponseDto<T>(
        [property: System.Text.Json.Serialization.JsonPropertyName("results")] IReadOnlyList<T>? Results,
        [property: System.Text.Json.Serialization.JsonPropertyName("page")] int? Page = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("totalPages")] int? TotalPages = null);

    /// <summary>
    /// Attaches the current API key to <paramref name="requestFactory"/>'s request and sends it.
    /// On a 401, forces exactly one API-key refresh and retries the same request once before
    /// surfacing the failure (PITFALLS.md #4's 401-triggered-retry recommendation).
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var apiKey = await _apiKeyProvider.GetApiKeyAsync(ct);
        var response = await SendWithApiKeyAsync(requestFactory, apiKey, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            apiKey = await _apiKeyProvider.ForceRefreshAsync(ct);
            response = await SendWithApiKeyAsync(requestFactory, apiKey, ct);
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task<HttpResponseMessage> SendWithApiKeyAsync(Func<HttpRequestMessage> requestFactory, string apiKey, CancellationToken ct)
    {
        using var request = requestFactory();
        request.Headers.Add(ApiKeyHeaderName, apiKey);
        return await _httpClient.SendAsync(request, ct);
    }
}
