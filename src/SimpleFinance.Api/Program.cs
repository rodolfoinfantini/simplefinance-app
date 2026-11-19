using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using SimpleFinance.Api.Endpoints;
using SimpleFinance.Api.Middleware;
using SimpleFinance.Application.Auth;
using SimpleFinance.Application.Auth.Setup;
using SimpleFinance.Infrastructure.Alerts;
using SimpleFinance.Infrastructure.Categorization;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Logging;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Pluggy;
using SimpleFinance.Infrastructure.Reporting;
using SimpleFinance.Infrastructure.Security;
using SimpleFinance.Infrastructure.Startup;
using SimpleFinance.Infrastructure.Sync;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Destructure.With<SecretRedactionDestructuringPolicy>()
    .WriteTo.Console()
    .WriteTo.File(
        "logs/simplefinance-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serialize enums as their string names in every Minimal-API JSON response (and accept them on
    // input). The React frontend types all enums as string unions (e.g. BudgetState = 'Ok' | 'Attention'
    // | 'Exceeded'); without this, raw enum-typed DTO properties (like BudgetService's State) serialize
    // as integers and the client crashes indexing a config map by the string name. String-typed status
    // properties are unaffected.
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Destructure.With<SecretRedactionDestructuringPolicy>()
        .WriteTo.Console()
        .WriteTo.File(
            "logs/simplefinance-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30));

    builder.Services.AddDbContext<SimpleFinanceDbContext>(options =>
        options.UseNpgsql(
            builder.Configuration.GetConnectionString("Default"),
            // AI-FND-03 (15-02): enable the Npgsql pgvector type handler so Phase 16 can map
            // Vector columns without touching startup again. Extension-enable only this phase.
            o => o.UseVector()));

    // --- Auth core (01-04): password hashing, JWT access tokens, rotating refresh tokens ---
    builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
    builder.Services.AddScoped<IPasswordHasher, Argon2idPasswordHasher>();
    builder.Services.AddScoped<IAccessTokenService, JwtAccessTokenService>();
    builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();

    // --- 2FA (01-05): TOTP + encrypted secret storage + single-use recovery codes ---
    builder.Services.Configure<TotpOptions>(builder.Configuration.GetSection(TotpOptions.SectionName));
    builder.Services.AddScoped<ITotpService, TotpService>();
    builder.Services.AddScoped<ITotpSecretProtector, AesTotpSecretProtector>();
    builder.Services.AddScoped<IRecoveryCodeService, RecoveryCodeService>();

    // --- Pluggy client core (02-02): API-key cache/refresh, Connect Token, item CRUD, MFA,
    // webhook registration. Single extension method — later Pluggy-adjacent plans (02-03/05/07)
    // extend AddPluggyClient itself rather than adding more lines here.
    builder.Services.AddPluggyClient(builder.Configuration);

    // --- AI foundation (15-03): single module entry point wiring the un-bypassable guardrail
    // internals — PiiRedactor (AI-FND-06), cost estimator + usage recorder (AI-FND-04), bound to
    // the "Ai" config section. Later AI plans (15-04 budget guard, 15-05 DeepSeek/OpenAI clients)
    // extend AddAiClients itself rather than adding more lines here.
    builder.Services.AddAiClients(builder.Configuration);

    // --- Sync reconciler (02-04): the ONE shared idempotent reconciler (ISyncReconciler) both
    // the webhook processor (02-05) and DailySyncJob/manual-sync (02-06) call, plus the
    // mapping-only categorization stub (SYNC-06) and the Postgres advisory lock (SYNC-07).
    builder.Services.AddSyncReconciler();

    // --- Reporting (05-04): Dapper connection factory + DashboardQueries, the shared aggregate
    // queries every Phase 5 Dashboard (05-06) and Budget (05-05) endpoint reads from.
    builder.Services.AddReporting();

    // --- Budget (05-05): BudgetService is the real, directly-testable business logic behind
    // GET/POST/DELETE /api/budgets -- BudgetsEndpoints.cs is a thin wrapper over it.
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Budgets.BudgetService>();

    // --- Dashboard (05-06): DashboardService is the real, directly-testable business logic
    // behind the 6 GET /api/dashboard/* routes -- DashboardEndpoints.cs is a thin wrapper over it.
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Dashboard.DashboardService>();

    // --- Investments (06-03): InvestmentsService is the real, directly-testable business logic
    // behind GET /api/investments and GET /api/investments/summary -- InvestmentsEndpoints.cs is
    // a thin wrapper over it.
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Investments.InvestmentsService>();

    // --- Net worth (06-04): NetWorthService is the real, directly-testable business logic behind
    // GET /api/networth/summary and GET /api/networth/evolution -- NetWorthEndpoints.cs is a thin
    // wrapper over it.
    builder.Services.AddScoped<SimpleFinance.Infrastructure.NetWorth.NetWorthService>();

    // --- Recurring (07-03, RECUR-01..04): config-backed detection thresholds + RecurringService,
    // the real, directly-testable business logic behind GET/PATCH /api/recurring/* --
    // RecurringEndpoints.cs is a thin wrapper over it. RecurringDetectionJob itself is registered
    // scoped via AddSyncJobs() below.
    builder.Services.Configure<SimpleFinance.Infrastructure.Recurring.RecurringOptions>(
        builder.Configuration.GetSection(SimpleFinance.Infrastructure.Recurring.RecurringOptions.SectionName));
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Recurring.RecurringService>();

    // --- Goals (07-04, GOAL-01..03): GoalService is the real, directly-testable business logic
    // behind GET/POST/PATCH /api/goals/* -- GoalsEndpoints.cs is a thin wrapper over it.
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Goals.GoalService>();

    // --- Reports (07-05, RPT-01/RPT-03): ReportsService assembles all 7 reports, reusing
    // DashboardQueries/ReportQueries/RecurringService wherever a suitable aggregate already
    // exists -- ReportsEndpoints.cs is a thin wrapper over it.
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Reports.ReportsService>();

    // --- Export (07-06, RPT-02): ExportService streams transactions and every one of 07-05's 7
    // reports as CSV (CsvHelper) and XLSX (ClosedXML) -- ExportEndpoints.cs is a thin wrapper
    // over it, and report exports reuse ReportsService's exact same methods (never a second,
    // divergent query path).
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Export.ExportService>();

    // --- Notifications (08-02, NOTIF-01): INotificationChannel abstraction (Telegram + Email),
    // both registered against the same interface so DI resolves IEnumerable<INotificationChannel>
    // with both automatically -- NotificationDispatcher picks the rule's configured channel(s)
    // and gracefully degrades to a no-op for any channel left unconfigured (common first-run
    // state, T-08-03).
    builder.Services.Configure<SimpleFinance.Infrastructure.Notifications.NotificationOptions>(
        builder.Configuration.GetSection(SimpleFinance.Infrastructure.Notifications.NotificationOptions.SectionName));
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Notifications.INotificationChannel, SimpleFinance.Infrastructure.Notifications.TelegramChannel>();
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Notifications.INotificationChannel, SimpleFinance.Infrastructure.Notifications.EmailChannel>();
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Notifications.NotificationDispatcher>();

    // --- Alert evaluation (08-04, NOTIF-02/NOTIF-03): AlertEvaluationOptions binds the
    // LargeTransaction lookback tunable; AlertEvaluationJob itself is registered scoped via
    // AddSyncJobs() below (mirrors NetWorthSnapshotJob/RecurringDetectionJob's registration
    // point) -- it is the sole evaluator of AlertRule rows into Notification rows + dispatch.
    builder.Services.Configure<SimpleFinance.Infrastructure.Alerts.AlertEvaluationOptions>(
        builder.Configuration.GetSection(SimpleFinance.Infrastructure.Alerts.AlertEvaluationOptions.SectionName));

    // --- Hangfire (02-05): first use in this codebase. Postgres-backed job storage/server for
    // WebhookProcessorJob (this plan) and DailySyncJob (02-06). Skipped entirely under the
    // "Testing" ASPNETCORE_ENVIRONMENT -- Hangfire.PostgreSql's storage doesn't work against EF
    // Core's InMemory provider used by WebApplicationFactory-based tests (mirrors the
    // Database.Migrate() guard further below). Test factories register a fake
    // IBackgroundJobClient in place of the real Hangfire server.
    if (!builder.Environment.IsEnvironment("Testing"))
    {
        builder.Services.AddHangfire(cfg => cfg
            .UsePostgreSqlStorage(options => options
                .UseNpgsqlConnection(builder.Configuration.GetConnectionString("Default"))));
        builder.Services.AddHangfireServer();
    }
    else
    {
        // 02-07: SOME IBackgroundJobClient registration must exist under "Testing" even for
        // WebApplicationFactory-derived test hosts that never touch background jobs (e.g.
        // AuthApiFactory) -- without one, ASP.NET Core's minimal-API parameter-source inference
        // can't classify an endpoint handler's IBackgroundJobClient parameter as a DI service,
        // and any handler that also has a genuine JSON body parameter (e.g. ConnectionsEndpoints'
        // RegisterConnectionAsync) then has two ambiguous "inferred body" candidates -- a hard
        // startup crash, not just a test inconvenience. Test factories that assert on enqueued
        // jobs (SyncApiFactory/WebhookApiFactory/ConnectionsApiFactory) override this with their
        // own fake via RemoveAll<IBackgroundJobClient>() + AddSingleton.
        builder.Services.AddSingleton<IBackgroundJobClient>(new NoOpBackgroundJobClient());
    }

    // --- Sync jobs (02-05): registers IWebhookProcessor -> WebhookProcessorJob. 02-06/07-03/08-04
    // extend this same AddSyncJobs() call rather than adding more lines here.
    builder.Services.AddSyncJobs();

    // --- One-time webhook subscription registration (02-05, SYNC-01): best-effort, swallows any
    // Pluggy-unavailability exception (never blocks/crashes startup), and no-ops entirely in
    // "Testing" or when no public webhook URL is configured yet (local dev).
    builder.Services.AddHostedService<WebhookRegistrationStartupTask>();

    // --- Category tree seeding (04-02, CAT-01): CategorySeeder is called synchronously from
    // AuthEndpoints.SetupAsync on first setup, plus CategorySeedStartupTask backstops any
    // existing user with zero categories on every app start (idempotent either way).
    builder.Services.AddScoped<CategorySeeder>();
    builder.Services.AddHostedService<CategorySeedStartupTask>();

    // --- Transfer detection backstop: runs ITransferDetectionService once at boot (idempotent,
    // respects IsTransferManualOverride) so newly added heuristics (e.g. same-account cofrinhos)
    // clean historical transactions immediately on deploy -- before recurring/alert boot passes.
    builder.Services.AddHostedService<TransferDetectionStartupTask>();

    // --- Net worth snapshot backstop (06-04, INV-04): writes an initial NetWorthSnapshot at boot
    // so the evolution chart has data immediately, not just after the first scheduled cron run.
    // NetWorthSnapshotJob itself is registered scoped via AddSyncJobs() above.
    builder.Services.AddHostedService<NetWorthSnapshotStartupTask>();

    // --- Recurring detection backstop (07-03, RECUR-01): writes an initial detection pass at
    // boot so the recurring panel isn't empty until the first scheduled `recurring-detection`
    // cron run (weekly by default). RecurringDetectionJob itself is registered scoped via
    // AddSyncJobs() above.
    builder.Services.AddHostedService<RecurringDetectionStartupTask>();

    // --- Alert evaluation backstop (08-04, NOTIF-02/NOTIF-03): runs an initial AlertEvaluationJob
    // pass at boot so the notification pipeline has already run once by the time the app finishes
    // booting, rather than waiting up to an hour for the first scheduled `alert-evaluation` cron
    // run. AlertEvaluationJob itself is registered scoped via AddSyncJobs() above.
    builder.Services.AddHostedService<AlertEvaluationStartupTask>();

    // Manual FluentValidation invocation only (IValidator<T> resolved and called explicitly by
    // each endpoint) — never FluentValidation.AspNetCore's auto-validation pipeline, which is
    // deprecated, MVC-only, and never worked with Minimal APIs (research/STACK.md).
    builder.Services.AddValidatorsFromAssemblyContaining<SetupUserValidator>();

    // 03-05: RecategorizeTransactionValidator/ApplySimilarValidator live in this (Api) assembly,
    // not Application, because they need SimpleFinanceDbContext and Application cannot reference
    // Infrastructure without a circular project reference (02-04-SUMMARY.md/03-02-SUMMARY.md
    // precedent) -- registered explicitly since AddValidatorsFromAssemblyContaining<SetupUserValidator>()
    // above only scans the Application assembly.
    builder.Services.AddScoped<IValidator<SimpleFinance.Application.Transactions.UpdateTransaction.RecategorizeTransactionCommand>, RecategorizeTransactionValidator>();
    builder.Services.AddScoped<IValidator<SimpleFinance.Application.Transactions.ApplySimilar.ApplySimilarCommand>, ApplySimilarValidator>();

    // 04-03: CreateCategoryValidator/UpdateCategoryValidator live in this (Api) assembly, not
    // Application, for the same circular-reference reason as 03-05's transaction validators
    // above (both need SimpleFinanceDbContext for the parentCategoryId existence+ownership
    // check) -- registered explicitly since AddValidatorsFromAssemblyContaining<SetupUserValidator>()
    // only scans the Application assembly. ToggleCategoryHideValidator has no DB dependency and
    // lives in Application, so it's already picked up by that same assembly scan.
    builder.Services.AddScoped<IValidator<SimpleFinance.Application.Categories.Create.CreateCategoryCommand>, CreateCategoryValidator>();
    builder.Services.AddScoped<IValidator<SimpleFinance.Application.Categories.Update.UpdateCategoryCommand>, UpdateCategoryValidator>();

    // 04-05: CreateCategoryRuleValidator/UpdateCategoryRuleValidator/ReorderCategoryRulesValidator
    // live in this (Api) assembly, not Application, for the same circular-reference reason as
    // 04-03's category validators above (all need SimpleFinanceDbContext -- categoryId ownership
    // for Create/Update, rule id-set completeness for Reorder). TestCategoryRuleValidator and
    // ToggleCategoryRuleActiveValidator have no DB dependency and live in Application, so they're
    // already picked up by the AddValidatorsFromAssemblyContaining<SetupUserValidator>() scan
    // above.
    builder.Services.AddScoped<IValidator<SimpleFinance.Application.CategoryRules.Create.CreateCategoryRuleCommand>, CreateCategoryRuleValidator>();
    builder.Services.AddScoped<IValidator<SimpleFinance.Application.CategoryRules.Update.UpdateCategoryRuleCommand>, UpdateCategoryRuleValidator>();
    builder.Services.AddScoped<IValidator<SimpleFinance.Application.CategoryRules.Reorder.ReorderCategoryRulesCommand>, ReorderCategoryRulesValidator>();

    // 07-04: CreateGoalValidator/UpdateGoalValidator live in this (Api) assembly, not
    // Application -- for goals, even the CreateGoalCommand/UpdateGoalCommand records themselves
    // live in GoalsEndpoints.cs (Api), not Application, since both validators need
    // SimpleFinanceDbContext for LinkedAccountId/LinkedInvestmentId ownership checks (same
    // circular-reference reason as 04-03's category validators above) -- registered explicitly
    // since AddValidatorsFromAssemblyContaining<SetupUserValidator>() only scans the Application
    // assembly.
    builder.Services.AddScoped<IValidator<CreateGoalCommand>, CreateGoalValidator>();
    builder.Services.AddScoped<IValidator<UpdateGoalCommand>, UpdateGoalValidator>();

    // 08-05: AlertRuleService is the real, directly-testable business logic behind GET/POST/PUT/
    // PATCH-active/DELETE /api/alert-rules -- AlertRulesEndpoints.cs is a thin wrapper over it.
    // CreateAlertRuleValidator/UpdateAlertRuleValidator live in this (Api) assembly, not
    // Application, for the same circular-reference reason as 07-04's goal validators above (both
    // need SimpleFinanceDbContext for CategoryId/AccountId ownership checks) -- registered
    // explicitly since AddValidatorsFromAssemblyContaining<SetupUserValidator>() only scans the
    // Application assembly.
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Alerts.AlertRuleService>();
    builder.Services.AddScoped<IValidator<CreateAlertRuleCommand>, CreateAlertRuleValidator>();
    builder.Services.AddScoped<IValidator<UpdateAlertRuleCommand>, UpdateAlertRuleValidator>();

    // 17-05 (AI-CHAT-01): ChatRequest/RenameConversationRequest contracts + their validators live in
    // this (Api) assembly (AiChatContracts.cs) — the chat endpoints wrap Infrastructure services
    // directly and have no Application-side command to attach to. Registered explicitly since
    // AddValidatorsFromAssemblyContaining<SetupUserValidator>() only scans the Application assembly.
    builder.Services.AddScoped<IValidator<ChatRequest>, ChatRequestValidator>();
    builder.Services.AddScoped<IValidator<RenameConversationRequest>, RenameConversationRequestValidator>();

    // 08-05: NotificationService is the real, directly-testable business logic behind
    // GET /api/notifications and POST /api/notifications/{id}/read -- NotificationsEndpoints.cs
    // is a thin wrapper over it. NotificationChannelsEndpoints.cs resolves
    // IEnumerable<INotificationChannel>/NotificationDispatcher directly (both already registered
    // by 08-02's "Notifications (08-02, NOTIF-01)" block above), no additional DI needed for it.
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Alerts.NotificationService>();

    // --- Preferences (08-06, CFG-02): PreferencesService is the real, directly-testable
    // business logic behind GET/PUT /api/preferences -- PreferencesEndpoints.cs is a thin
    // wrapper over it. Updating the sync-cron field live-reschedules the already-running
    // Hangfire "daily-sync" recurring job (skipped under "Testing", mirrors every other
    // Hangfire-touching code path above).
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Preferences.PreferencesService>();

    // --- Backup (08-06, CFG-03): BackupService streams a full JSON dump of the caller's own
    // financial data (never Users/RefreshTokens/RecoveryCodes) -- BackupEndpoints.cs is a thin
    // wrapper over it, rate-limited via the "export" policy below.
    builder.Services.AddScoped<SimpleFinance.Infrastructure.Backup.BackupService>();

    // JWT bearer auth for [Authorize]-gated endpoints (change-password today; more land in
    // later phases). MapInboundClaims = false so the "sub" claim is read as-is instead of
    // JwtSecurityTokenHandler's default remap to ClaimTypes.NameIdentifier — the same gotcha
    // 01-03 hit and documented in its own JwtAccessTokenService tests.
    //
    // JwtBearerOptions is configured via AddOptions<T>().Configure<IOptions<JwtOptions>>(...)
    // rather than reading builder.Configuration directly inside AddJwtBearer(options => ...):
    // the latter would capture the signing key BEFORE builder.Build() finishes composing all
    // configuration sources (e.g. AuthApiFactory's test overrides, applied only once the host
    // is actually built), silently binding an empty key. The Configure<TDep> overload resolves
    // IOptions<JwtOptions> from DI lazily, on first use, after the host — and its final
    // configuration — is fully built.
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer();

    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<Microsoft.Extensions.Options.IOptions<JwtOptions>>((options, jwtOptions) =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Value.SigningKey)),
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });

    builder.Services.AddAuthorization();

    // Frontend (Vite dev server, localhost:5173) and API (localhost:5080) are different origins
    // in local dev — AllowCredentials is required so the browser attaches the httpOnly refresh
    // cookie to the SPA's cross-origin requests. In production, front/api will likely sit
    // behind the same StackForge-provisioned origin/reverse-proxy path, but this stays harmless
    // same-origin too.
    const string FrontendCorsPolicy = "Frontend";
    var corsAllowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173";

    builder.Services.AddCors(options =>
    {
        options.AddPolicy(FrontendCorsPolicy, policy => policy
            .WithOrigins(corsAllowedOrigin)
            .WithMethods("GET", "POST", "PATCH", "DELETE")
            .WithHeaders("Content-Type", "Authorization")
            .AllowCredentials());
    });

    // PRD §7.2: rate-limit the login endpoint specifically, partitioned per client IP so one
    // caller can't exhaust another's quota — 10 attempts/minute is generous for a legitimate
    // single user while still throttling credential-stuffing/brute-force attempts (T-01-11).
    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // SYNC-01/T-02-14: separate, more generous policy for Pluggy's inbound webhook calls --
        // its own retry cadence (up to 9x/~3h on failure) plus normal event volume could burst
        // well past the tight 10/min "login" policy, and this endpoint does zero synchronous
        // heavy work regardless of volume.
        options.AddPolicy("webhook", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // CFG-03/T-08-14: a full-data-dump endpoint is meaningfully more expensive than a
        // typical read and a natural scrape target even for a single-user app, so it gets its
        // own tighter policy rather than relying on the absence of any rate limit.
        options.AddPolicy("export", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limited" }, ct);
        };
    });

    var app = builder.Build();

    // Only log request path/method/status/duration — never raw request/response bodies
    // (PITFALLS.md #12: HTTP body logging middleware is a common secret-leak vector, so it is
    // intentionally never enabled here).
    app.UseSerilogRequestLogging();

    // Centralizes FluentValidation.ValidationException -> { errors: {...} } 400 mapping across
    // every Auth endpoint that calls ValidateAndThrowAsync (setup, login, change-password).
    app.UseValidationExceptionHandling();

    // CORS must run after routing but before authorization (ASP.NET Core ordering guidance).
    app.UseCors(FrontendCorsPolicy);

    app.UseRateLimiter();

    app.UseAuthentication();
    app.UseAuthorization();

    // T-02-15: Hangfire dashboard requires an authenticated JWT principal -- placed after
    // UseAuthentication()/UseAuthorization() above so HangfireAuthorizationFilter's
    // HttpContext.User check observes a populated principal. Skipped under "Testing" (Hangfire
    // itself isn't registered in that environment -- see the AddHangfire guard above).
    if (!app.Environment.IsEnvironment("Testing"))
    {
        app.MapHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = [new HangfireAuthorizationFilter()],
        });

        // --- DailySyncJob (02-06, SYNC-02): configurable 2x/day cron backstop over every active
        // Connection, calling the SAME ISyncReconciler every other sync trigger calls.
        // Registered here (not earlier) because RecurringJob.AddOrUpdate requires live Hangfire
        // storage, unavailable under the Testing/InMemory setup this whole block is skipped
        // under. Default "0 6,18 * * *" = 06:00 and 18:00 SERVER-LOCAL time (PRD §6.3/
        // RF-SYNC-002's example) -- not yet timezone-aware; PITFALLS.md #8 flags this as a
        // future-phase improvement once RF-CFG-004's user timezone preference exists (out of
        // Phase 2 scope).
        var dailySyncCronExpression = builder.Configuration["Sync:DailySyncCronExpression"] ?? "0 6,18 * * *";
        RecurringJob.AddOrUpdate<DailySyncJob>(
            "daily-sync",
            job => job.ExecuteAsync(CancellationToken.None),
            dailySyncCronExpression);

        // --- NetWorthSnapshotJob (06-04, INV-04): daily recurring upsert of one NetWorthSnapshot
        // per user, the sole writer of the table the Phase 5 dashboard's evolution chart and
        // 06-06's Net Worth screen both read from. Default "30 0 * * *" = 00:30 server-local time
        // (06-CONTEXT.md's suggested example) -- after midnight so the snapshot reflects the full
        // prior day's activity. NetWorthSnapshotStartupTask above already backstops an initial
        // snapshot at boot, so the evolution chart never waits up to 24h for this cron's first run.
        var netWorthSnapshotCronExpression = builder.Configuration["NetWorth:SnapshotCronExpression"] ?? "30 0 * * *";
        RecurringJob.AddOrUpdate<NetWorthSnapshotJob>(
            "net-worth-snapshot",
            job => job.ExecuteAsync(CancellationToken.None),
            netWorthSnapshotCronExpression);

        // --- RecurringDetectionJob (07-03, RECUR-01): weekly recurring idempotent upsert of
        // RecurringTransaction rows, the sole writer of the table the recurring panel reads from.
        // Default "0 3 * * 1" = 03:00 server-local every Monday (07-CONTEXT.md's suggested
        // cadence). RecurringDetectionStartupTask above already backstops an initial detection
        // pass at boot, so the panel never waits up to a week for this cron's first run.
        var recurringDetectionCronExpression =
            builder.Configuration["Recurring:DetectionCronExpression"] ?? "0 3 * * 1";
        RecurringJob.AddOrUpdate<SimpleFinance.Infrastructure.Recurring.RecurringDetectionJob>(
            "recurring-detection",
            job => job.ExecuteAsync(CancellationToken.None),
            recurringDetectionCronExpression);

        // --- AlertEvaluationJob (08-04, NOTIF-02/NOTIF-03): the sole evaluator of AlertRule rows
        // into Notification rows + dispatch, covering all 7 alert types. Default "0 * * * *" =
        // hourly -- safe regardless of cadence since every evaluator's AlertFireLog de-dupe key
        // already prevents re-alerting within its own natural period (90/100% budget tiers,
        // once-per-day low-balance/sync-error, once-per-billing-cycle invoice-due, once-ever
        // large-transaction/new-recurring/goal-reached), so an hourly cadence catches every
        // condition promptly without needing a different schedule per alert type.
        // AlertEvaluationStartupTask above already backstops an initial evaluation pass at boot,
        // so the notification pipeline never waits up to an hour for this cron's first run.
        var alertEvaluationCronExpression =
            builder.Configuration["Alerts:EvaluationCronExpression"] ?? "0 * * * *";
        RecurringJob.AddOrUpdate<SimpleFinance.Infrastructure.Alerts.AlertEvaluationJob>(
            "alert-evaluation",
            job => job.ExecuteAsync(CancellationToken.None),
            alertEvaluationCronExpression);

        // --- AiEmbeddingJob (16-02, AI-CAT-01): nightly vectorization of transactions that have no
        // TransactionEmbedding row yet, so the 16-03 kNN cascade has vectors to compare against. The
        // job is also enqueued post-sync in 16-04; this recurring registration is the backstop that
        // guarantees the embedding index fills even if a post-sync enqueue is missed. Default
        // "0 3 * * *" = 03:00 server-local (cheap: hash-cached, batched <=100, budget-guarded). It
        // degrades gracefully on AiBudgetExceededException and no-ops when AI/categorization are off.
        var aiEmbeddingCronExpression = builder.Configuration["Ai:EmbeddingCronExpression"] ?? "0 3 * * *";
        RecurringJob.AddOrUpdate<SimpleFinance.Infrastructure.Jobs.AiEmbeddingJob>(
            "ai-embedding",
            job => job.ExecuteAsync(CancellationToken.None),
            aiEmbeddingCronExpression);

        // --- AiCategorizationJob (16-04, AI-CAT-03/04/05): nightly pass over the "Sem categoria"
        // backlog, applying the 16-03 cascade's hybrid-by-confidence rule (auto-apply as AiAuto at/above
        // the user's threshold, else a Pending AiSuggestion), never overwriting Rule/UserOverride/
        // AiSuggested/AiAuto rows. Also enqueued post-sync (after ai-embedding) so new transactions get
        // categorized as they arrive; this recurring registration is the backstop. Default "30 3 * * *"
        // = 03:30 server-local, AFTER the 03:00 ai-embedding run so fresh vectors exist for the kNN level.
        var aiCategorizationCronExpression = builder.Configuration["Ai:CategorizationCronExpression"] ?? "30 3 * * *";
        RecurringJob.AddOrUpdate<SimpleFinance.Infrastructure.Jobs.AiCategorizationJob>(
            "ai-categorization",
            job => job.ExecuteAsync(CancellationToken.None),
            aiCategorizationCronExpression);

        // --- AiAnomalyJob (18-05, AI-INS-02/03): nightly backstop for the statistical anomaly detector
        // (per-merchant z-score, duplicate charge, subscription increase, atypical category). Persists
        // Kind=Anomaly cards for every user with AiEnabled && AiAnomalyEnabled and, for high-severity
        // z-score/duplicate anomalies ONLY, fires a fire-once AiAnomaly Notification via NotificationDispatcher
        // (subscription-increase + atypical-category stay card-only to avoid double-notifying with Phase 8
        // alerts). Also enqueued post-sync (after ai-categorization) in EnqueueAiCascade; this recurring
        // registration is the backstop for a missed post-sync enqueue. Default "0 4 * * *" = 04:00 server-local,
        // BEFORE the 04:30 ai-insight run (RESEARCH Q6). Idempotent via the AiInsight (UserId, DedupKey) index.
        var aiAnomalyCronExpression = builder.Configuration["Ai:AnomalyCronExpression"] ?? "0 4 * * *";
        RecurringJob.AddOrUpdate<SimpleFinance.Infrastructure.Jobs.AiAnomalyJob>(
            "ai-anomaly",
            job => job.ExecuteAsync(CancellationToken.None),
            aiAnomalyCronExpression);

        // --- AiInsightJob (18-03, AI-INS-01): daily pass that detects the deterministic insight signals
        // (SpendingChange/Trend/BudgetRisk/SubscriptionCreep), batch-phrases them via one id-keyed
        // ChatAsync (degrading to deterministic fallback prose when over budget), and Upserts them per user
        // with AiEnabled && AiInsightsEnabled. Idempotent via the AiInsight (UserId, DedupKey) unique index.
        // Daily-only by design (CONTEXT) — NOT chained into the post-sync AI cascade. Default "30 4 * * *"
        // = 04:30 server-local, AFTER the 03:30 ai-categorization run so insights reflect fresh categories.
        var aiInsightCronExpression = builder.Configuration["Ai:InsightCronExpression"] ?? "30 4 * * *";
        RecurringJob.AddOrUpdate<SimpleFinance.Infrastructure.Jobs.AiInsightJob>(
            "ai-insight",
            job => job.ExecuteAsync(CancellationToken.None),
            aiInsightCronExpression);

        // --- AiForecastJob (19-02, AI-FCT-01/02): daily pass that computes the deterministic current-month
        // projection (income/expense/balance + variance range + per-category drivers), attaches an optional
        // deepseek-chat narrative (degrading to null when over budget), and REPLACE-upserts the Forecast per
        // user with AiEnabled && AiForecastEnabled. Unlike the insight/anomaly jobs it OVERWRITES the
        // (UserId, Month) row each run so the projection tracks the month (never freezes at day-1 — RESEARCH
        // Pitfall 1). Default "0 5 * * *" = 05:00 server-local, AFTER the 04:30 ai-insight run.
        var aiForecastCronExpression = builder.Configuration["Ai:ForecastCronExpression"] ?? "0 5 * * *";
        RecurringJob.AddOrUpdate<SimpleFinance.Infrastructure.Jobs.AiForecastJob>(
            "ai-forecast",
            job => job.ExecuteAsync(CancellationToken.None),
            aiForecastCronExpression);

        // AI-FCT-03/04: the DAY-1 monthly summary job — for every user with AiEnabled &&
        // AiMonthlySummaryEnabled it builds the deterministic prior-month fact package, asks
        // deepseek-reasoner (the ONE premium call/month) to phrase a PT-BR summary (degrading to a
        // deterministic fallback over budget), persists it SKIP-if-exists (idempotent once-per-month via the
        // AiInsight (UserId, DedupKey) index), and fires the notification exactly once on the inserting run.
        // Default "0 5 1 * *" = 05:00 server-local on the 1st.
        var aiMonthlySummaryCronExpression = builder.Configuration["Ai:MonthlySummaryCronExpression"] ?? "0 5 1 * *";
        RecurringJob.AddOrUpdate<SimpleFinance.Infrastructure.Jobs.AiMonthlySummaryJob>(
            "ai-monthly-summary",
            job => job.ExecuteAsync(CancellationToken.None),
            aiMonthlySummaryCronExpression);
    }

    // FND-05: migrations apply automatically on startup. Safe here because this is a
    // single-instance deployment (research/ARCHITECTURE.md) — never run multiple API
    // replicas against the same database concurrently. Skipped under the "Testing" environment
    // (integration tests run against EF Core's InMemory provider, which doesn't support
    // relational migrations — see AuthApiFactory in tests/SimpleFinance.UnitTests).
    if (!app.Environment.IsEnvironment("Testing"))
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        dbContext.Database.Migrate();
    }

    app.MapGet("/health", async (SimpleFinanceDbContext dbContext) =>
    {
        var canConnect = await dbContext.Database.CanConnectAsync();

        return canConnect
            ? Results.Ok(new { status = "healthy", database = "connected" })
            : Results.Json(
                new { status = "unhealthy", database = "unreachable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    });

    app.MapAuthEndpoints();
    app.MapTwoFactorEndpoints();
    app.MapWebhookEndpoints();
    app.MapSyncEndpoints();
    app.MapConnectionsEndpoints();
    app.MapAccountsEndpoints();
    app.MapTransactionsEndpoints();
    app.MapCategoriesEndpoints();
    app.MapCategoryRulesEndpoints();
    app.MapBudgetsEndpoints();
    app.MapDashboardEndpoints();
    app.MapInvestmentsEndpoints();
    app.MapNetWorthEndpoints();
    app.MapRecurringEndpoints();
    app.MapGoalsEndpoints();
    app.MapReportsEndpoints();
    app.MapExportEndpoints();
    app.MapAlertRulesEndpoints();
    app.MapNotificationsEndpoints();
    app.MapNotificationChannelsEndpoints();
    app.MapPreferencesEndpoints();
    app.MapAiEndpoints();
    app.MapBackupEndpoints();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "SimpleFinance.Api terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Exposes the top-level-statement-generated Program class to
// Microsoft.AspNetCore.Mvc.Testing's WebApplicationFactory<Program> in the test project
// (that class is `internal` by default and invisible across assemblies otherwise).
public partial class Program
{
}
