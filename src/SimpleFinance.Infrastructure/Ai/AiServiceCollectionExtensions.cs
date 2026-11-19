using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Application.Categorization;
using SimpleFinance.Infrastructure.Ai.Assistant.Tools;
using SimpleFinance.Infrastructure.Ai.Anthropic;
using SimpleFinance.Infrastructure.Ai.DeepSeek;
using SimpleFinance.Infrastructure.Ai.OpenAi;
using SimpleFinance.Infrastructure.Ai.OpenRouter;
using SimpleFinance.Infrastructure.Ai.Routing;
using SimpleFinance.Infrastructure.Ai.Usage;
using SimpleFinance.Infrastructure.Categorization;

namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// Single DI composition entry point for the AI foundation module (mirrors
/// PluggyServiceCollectionExtensions' one-block-per-module pattern). This is the ONLY block later
/// AI plans extend — 15-04 adds the budget guard, 15-05 adds the DeepSeek/OpenAI typed HttpClients —
/// so Program.cs keeps exactly one AI wiring line. Registers the guardrail internals the clients
/// depend on: the PII redactor (AI-FND-06), the cost estimator + usage recorder (AI-FND-04), all
/// bound to the "Ai" config section.
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAiClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));

        // Un-bypassable PII scrub — pure/stateless utility, safe as a Singleton (AI-FND-06).
        services.AddSingleton<IPiiRedactor, PiiRedactor>();

        // Stateless config-driven rate lookup — Singleton (AI-FND-04).
        services.AddSingleton<AiCostEstimator>();

        // Writes one AiUsageLog per call — Scoped (touches SimpleFinanceDbContext) (AI-FND-04).
        services.AddScoped<IAiUsageRecorder, AiUsageRecorder>();

        // Pre-flight monthly budget kill-switch — Scoped (touches SimpleFinanceDbContext) (AI-FND-05).
        // The 15-05 clients inject IAiBudgetGuard to enforce redact → budget-check → send → record.
        services.AddScoped<IAiBudgetGuard, AiBudgetGuard>();

        // AI settings get-or-create read/update + key-present projection — Scoped (touches
        // SimpleFinanceDbContext). Backs GET/PUT /api/ai/settings; reports key presence only, never
        // the key value (AI-FND-07/08, T-15-14).
        services.AddScoped<AiSettingsService>();

        // Runtime provider-key resolver (DB-decrypt-else-env-trim) — Scoped (touches
        // SimpleFinanceDbContext + ITotpSecretProtector). The bearer handlers below resolve it per
        // request through a fresh scope; AiSettingsService/SemanticSearchTool consume it directly.
        services.AddScoped<IAiKeyProvider, AiKeyProvider>();

        // Per-request Authorization injectors for the DeepSeek/OpenAI/OpenRouter HttpClients are
        // registered with the provider clients below. Transient — the HttpClient factory owns their
        // lifetime; each resolves the current key via IAiKeyProvider on every SendAsync.

        // AI suggestion review-loop operations (list/accept/reject/bulk-accept) — Scoped (touches
        // SimpleFinanceDbContext). Backs GET /api/ai/suggestions + the accept/reject/bulk-accept
        // endpoints; every op is UserId-scoped and accepting applies CategorySource=AiSuggested
        // (AI-CAT-06, T-16-11/12).
        services.AddScoped<AiSuggestionService>();

        // AI-INS-01/03/04: the shared insight/anomaly write/read spine (dedup-aware Upsert + New/Seen/
        // Dismissed lifecycle) — Scoped (touches SimpleFinanceDbContext). Both Phase 18 jobs call
        // UpsertAsync (idempotent via the unique (UserId,DedupKey) index); the insights endpoints call
        // GetAsync/DismissAsync/MarkSeenAsync. Every op is UserId-scoped (T-18-01).
        services.AddScoped<Insights.AiInsightService>();

        // AI-FCT-01: the UserId-scoped month-forecast write/read spine (19-01) — Scoped (touches
        // SimpleFinanceDbContext). UpsertAsync REPLACES the current-month (UserId, Month) row in place so
        // the daily AiForecastJob overwrites the projection each run (never freezes it — RESEARCH
        // Pitfall 1), unlike AiInsightService's skip-if-exists gate. GetAsync backs GET /api/ai/forecast.
        services.AddScoped<Forecast.ForecastService>();

        // AI-FCT-01: the pure, deterministic month-forecast calculator (19-02) — projects
        // income/expense/balance + variance range + per-category drivers (ByCategoryJson) by reusing the
        // existing reader bridges (IPeriodTotalsReader / ICategorySpendReader) + RecurringTransaction fields
        // verbatim, never a second formula. Numbers never touch the LLM. Scoped (touches
        // SimpleFinanceDbContext). The daily AiForecastJob calls ComputeAsync then attaches an optional
        // narrative and REPLACE-upserts via ForecastService.
        services.AddScoped<Forecast.ForecastCalculator>();

        // AI-FCT-02: the skippable LLM narrative layer for the forecast (19-02) — one deepseek-chat
        // ChatAsync (AiFeature.Forecast) that phrases 1-2 PT-BR sentences explaining the ALREADY-computed
        // figures, degrading to a null Narrative on AiBudgetExceededException or any failure (the forecast
        // persists regardless; numbers stay deterministic). Scoped (depends on the Scoped ILlmClient).
        services.AddScoped<Forecast.ForecastNarrativeWriter>();

        // AI-INS-01: the pure, deterministic insight-signal detector (18-02) — computes the qualifying
        // SpendingChange (incl. new-merchant fold) / Trend / BudgetRisk / SubscriptionCreep candidates by
        // reusing the existing reader bridges (ICategorySpendReader/IBudgetReader/INetWorthReader) +
        // RecurringTransaction fields, never a second formula. No LLM, no persistence; 18-03's job feeds its
        // output to the prose layer + AiInsightService.UpsertAsync. Scoped (touches SimpleFinanceDbContext).
        services.AddScoped<Insights.IInsightDetector, Insights.InsightDetector>();

        // AI-INS-02: the pure, deterministic statistical anomaly detector (18-04) — produces Kind=Anomaly
        // InsightSignals for per-merchant modified z-score outliers, duplicate charges, subscription
        // increases (reusing RecurringTransaction fields), and atypical category spend. Debit-only scope +
        // the RecurringTransaction allowlist + absolute floors are the false-positive guards (income never
        // flagged). No LLM, no persistence; 18-05's anomaly job feeds its output to the prose layer +
        // AiInsightService.UpsertAsync and dispatches notifications. Scoped (touches SimpleFinanceDbContext).
        services.AddScoped<Insights.IAnomalyDetector, Insights.AnomalyDetector>();

        // AI-INS-01: the skippable LLM prose layer (18-03) — batches a run's InsightSignals into ONE
        // id-keyed ChatAsync (AiFeature.Insights) to phrase Title/Body, degrading to the detector's
        // deterministic fallback on AiBudgetExceededException or any failure (persistence never depends on
        // the LLM; numbers stay in DataJson). Scoped (depends on the Scoped ILlmClient + IPiiRedactor).
        services.AddScoped<Insights.InsightProseWriter>();

        // AI-FCT-03: the deterministic prior-month fact-package builder for the monthly summary (19-03) —
        // assembles totals+MoM / category+MoM / budget / net-worth / subscriptions / top-merchants into one
        // jsonb DataJson by reusing the existing reader bridges (IPeriodTotalsReader/ICategorySpendReader/
        // IBudgetReader/ITopMerchantsReader) + NetWorthSnapshots/RecurringTransactions verbatim. Every merchant
        // string is redacted at source (IPiiRedactor). Numbers never touch the LLM. Scoped (touches the DbContext).
        services.AddScoped<Insights.MonthlySummaryFactBuilder>();

        // AI-FCT-03: the skippable deepseek-reasoner summary layer (19-03) — one ChatAsync
        // (AiFeature.MonthlySummary) that phrases 3-5 PT-BR destaques + 2-3 sugestões over the fact package,
        // reading result.Content ONLY (never ReasoningContent — Pitfall 4 / T-19-11), degrading to the
        // deterministic fallback Title/Body on AiBudgetExceededException / any failure (the summary persists
        // regardless; numbers stay deterministic). Scoped (depends on the Scoped ILlmClient).
        services.AddScoped<Insights.MonthlySummarizer>();

        // AI-CHAT-01: UserId-scoped conversation store (create/list/get-with-messages/rename/delete/
        // append) behind the chat endpoints (17-05) and the orchestrator's turn-persistence (17-04) —
        // Scoped (touches SimpleFinanceDbContext).
        services.AddScoped<Assistant.ConversationService>();

        // AI-CHAT-02: the assistant read-tool layer — a name→handler dispatcher over a catalog of
        // UserId-scoped, parameterized reporting tools advertised to the LLM as JSON schemas. The
        // catalog is built by DI from every registered IAssistantTool (read tools now; the RAG tool in
        // 17-03 and the write tools in 17-06 register into the same catalog). All Scoped — the tools
        // touch SimpleFinanceDbContext / Scoped reporting services. The seven read tools + their
        // reporting-service bridges are registered by AddAssistantReadTools (Assistant/Tools).
        services.AddScoped<ToolCatalog>();
        services.AddScoped<Assistant.ToolDispatcher>();
        services.AddAssistantReadTools();

        // AI-CHAT-04: the safety spine for the assistant's WRITE path. The four write tools only ever
        // PROPOSE — they stage an AiPendingAction(Pending) via AiPendingActionService and never mutate a
        // domain table. AiPendingActionService.ConfirmAsync is the ONLY path that executes, dispatching by
        // ActionType through PendingActionExecutor to the existing domain services (GoalService /
        // BudgetService / CategoryRule create / bulk recategorize). All Scoped (touch SimpleFinanceDbContext
        // + the Scoped Goal/Budget services); the write tools register into the same ToolCatalog via
        // AddAssistantReadTools.
        services.AddScoped<Assistant.PendingActionExecutor>();
        services.AddScoped<Assistant.AiPendingActionService>();

        // AI-CHAT-02/05: the agentic orchestrator — the bounded ChatAsync tool loop + streamed final
        // answer + per-turn AiMessage persistence + grounding/budget/truncation. Yields
        // IAsyncEnumerable<SseItem<ChatEvent>> for the SSE endpoint (17-05). Scoped (depends on the
        // Scoped catalog/dispatcher/ConversationService/AiSettingsService + the ILlmClient).
        services.AddScoped<Assistant.AssistantOrchestrator>();

        // --- Categorization cascade (16-03, AI-CAT-02/03) ---
        // The pgvector kNN voter (level 3) + the read-only 4-level cascade that returns a pure
        // CategoryClassification. Both Scoped (touch SimpleFinanceDbContext). The cascade reuses the
        // deterministic ICategorizationService (CategoryRuleEngine, registered in AddSyncServices) for
        // levels 1-2. 16-04's job consumes IAiCategorizationService and applies the decision.
        services.AddScoped<IKnnCategoryVoter, KnnCategoryVoter>();
        services.AddScoped<IAiCategorizationService, AiCategorizationService>();

        // --- Provider clients (15-05; Anthropic/OpenRouter routing added later) ---
        // Chat goes through RoutingLlmClient, which picks the concrete client per call from the model
        // name (claude-* → Anthropic, vendor/model slug → OpenRouter, else DeepSeek). The concrete
        // clients are registered as their own types; only the router is exposed as ILlmClient.
        //
        // DeepSeek chat (AI-FND-01) — typed HttpClient with the standard resilience handler
        // (retry/backoff/circuit-breaker), cloning the PluggyClient registration shape. The Bearer key
        // is NO LONGER baked here: DeepSeekBearerHandler injects it per request (DB-decrypt-else-env,
        // trimmed), so a key edited in Settings → IA takes effect without a restart. ChatAsync/EmbedAsync
        // use these resilient clients; the streaming path uses the separate relaxed client registered below.
        services.AddTransient<DeepSeekBearerHandler>();
        services.AddTransient<OpenAiBearerHandler>();
        services.AddTransient<OpenRouterBearerHandler>();

        services.AddHttpClient<DeepSeekLlmClient>((sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            client.BaseAddress = new Uri(o.DeepSeek.BaseUrl);
        }).AddHttpMessageHandler<DeepSeekBearerHandler>().AddStandardResilienceHandler();

        // OpenRouter chat — same OpenAI-compatible client shape as DeepSeek (shared base class), its own
        // base URL + per-request OpenRouterBearerHandler. No attribution headers are configured.
        // Without AddStandardResilienceHandler so its 10s default AttemptTimeout does not abort
        // thinking models (e.g. Gemini 3+ via OpenAI-compatible endpoint) mid-tool-call.
        services.AddHttpClient<OpenRouterLlmClient>((sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            client.BaseAddress = new Uri(o.OpenRouter.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).AddHttpMessageHandler<OpenRouterBearerHandler>();

        // Anthropic (Claude) chat via the official SDK. The SDK gets a named HttpClient WITHOUT the
        // standard resilience handler (the SDK retries itself; the handler's timeouts would abort long
        // thinking/streaming calls) and with the HttpClient timeout disabled (the SDK applies its own).
        // The API key is resolved per call by AnthropicLlmClient via IAiKeyProvider.
        services.AddHttpClient(AnthropicLlmClient.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddScoped<AnthropicLlmClient>();

        services.AddTransient<ILlmClient>(sp => new RoutingLlmClient(
            new Lazy<DeepSeekLlmClient>(sp.GetRequiredService<DeepSeekLlmClient>),
            new Lazy<AnthropicLlmClient>(sp.GetRequiredService<AnthropicLlmClient>),
            new Lazy<OpenRouterLlmClient>(sp.GetRequiredService<OpenRouterLlmClient>)));

        // OpenAI embeddings (AI-FND-02) — typed HttpClient, resilience ON (60s attempt timeout for
        // large batches and OpenAI-compatible providers like Google AI Studio). Key injected per
        // request by OpenAiBearerHandler (no static header).
        services.AddHttpClient<IEmbeddingClient, OpenAiEmbeddingClient>((sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            client.BaseAddress = new Uri(o.OpenAi.BaseUrl);
        }).AddHttpMessageHandler<OpenAiBearerHandler>().AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(180);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(180);
        });

        // SEPARATE streaming clients for DeepSeek/OpenRouter SSE (ChatStreamAsync). Deliberately WITHOUT
        // AddStandardResilienceHandler: the standard handler's total-request timeout aborts long-lived
        // SSE reads mid-stream and its retries cannot replay a partially consumed stream (RESEARCH
        // Pitfall 3 / T-15-12). Timeout is disabled so the stream can run for its full lifetime; each
        // client resolves its own by name via IHttpClientFactory only for streaming. The same
        // per-request bearer handler injects the key.
        services.AddHttpClient(DeepSeekLlmClient.StreamingHttpClientName, (sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            client.BaseAddress = new Uri(o.DeepSeek.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).AddHttpMessageHandler<DeepSeekBearerHandler>();

        services.AddHttpClient(OpenRouterLlmClient.StreamingHttpClientName, (sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            client.BaseAddress = new Uri(o.OpenRouter.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).AddHttpMessageHandler<OpenRouterBearerHandler>();

        return services;
    }
}
