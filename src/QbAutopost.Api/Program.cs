using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Health;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Ocr;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Security;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Api;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Gateway;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Security;
using QbAutopost.Core.Store;
using QbAutopost.QuickBooks;

// T-901 (api-v1 §12): read appsettings.json from the exe's folder, not the working directory (server defect 1).
// The environment is read from the variable because the host does not exist yet; the test host keeps its own root.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = ContentRoot.Select(
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        AppContext.BaseDirectory),
});

// Secrets may come from QBAUTOPOST__Section__Key environment variables (spec §12).
builder.Configuration.AddEnvironmentVariables(prefix: "QBAUTOPOST__");
builder.AddQbAutopostLogging();
builder.WebHost.UseUrls(builder.Configuration["Api:Bind"] ?? new ApiSettings().Bind);

// T-911 (FR-A-14, FR-A-17): no Server header, a body cap Kestrel enforces before anything is read, and HTTPS when a
// certificate is configured. Only the real host uses Kestrel — the test server ignores all of this, which is why the
// decisions above it are tested as decisions (TransportGuard) rather than through a socket.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var api = context.Configuration.GetSection("Api").Get<ApiSettings>() ?? new ApiSettings();
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = api.MaxRequestBodyBytes;
    if (api.Tls.Configured)
    {
        kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = TlsCertificate.Load(api.Tls));
    }
});

var contentRoot = builder.Environment.ContentRootPath;
builder.Services.AddOptions<AppSettings>()
    .Bind(builder.Configuration)
    .PostConfigure(s => s.ResolvePaths(contentRoot));

const string CorsPolicyName = "QbAutopostCallers";

// T-912 (api-v1 §2.6): the correlation id is part of every problem detail, so a caller reporting a failure can
// quote one id that appears in the header, the log and the audit file.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
{
    if (RequestId.Of(ctx.HttpContext) is { } id)
    {
        ctx.ProblemDetails.Extensions[RequestId.PropertyName] = id;
    }
});
// T-911 (FR-A-14): CORS stays off until an origin is named; '*' never gets this far (TransportGuard refuses it).
var corsOrigins = builder.Configuration.GetSection("Api:Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddPolicy(
        CorsPolicyName,
        p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));
}
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<IClock, SystemClock>();
// T-910 (FR-A-13): the caller list is re-read per request, so revoking a key takes effect without a restart.
builder.Services.AddSingleton(sp => new ApiClientStore(sp.GetRequiredService<IOptions<AppSettings>>().Value.Paths.Clients));
builder.Services.AddScoped(sp => new ApiClientResolver(sp.GetRequiredService<ApiClientStore>().Load()));
// T-911 (FR-A-15): the built-in limiter, and the brute-force brake the limiter cannot cover — a request refused at
// the key check never reaches the limiter, so guessing keys has to be counted where the guess is read.
builder.Services.AddRateLimiter(RateLimitPolicy.Configure);
builder.Services.AddSingleton(sp => new AuthBrake(
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<IOptions<AppSettings>>().Value.Api.RateLimits));
// T-912 (FR-A-16): beside the operational log, but not a sink of it — no level, no filter, no formatter to lose it to.
builder.Services.AddSingleton(sp =>
{
    var s = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    return new AuditLog(s.Paths.Logs, s.Api.AuditRetentionDays);
});
builder.Services.AddSingleton<LegacyRouteLog>();
builder.Services.AddSingleton<AppHealth>();
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton(sp =>
{
    var h = sp.GetRequiredService<IOptions<AppSettings>>().Value.Hermes;
    return new HermesOptions
    {
        BaseUrl = h.BaseUrl,
        ApiKey = h.ApiKey,
        Model = h.Model,
        Timeout = TimeSpan.FromSeconds(h.TimeoutSeconds),
    };
});
// HermesOptions.Timeout bounds each call; the HttpClient's own 100 s default would cut the 120 s budget short.
// A singleton reader holds its HermesClient, so the handler is never rotated (Hermes is a fixed loopback address).
builder.Services.AddHttpClient<HermesClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
// T-806: Hermes:Enabled=false runs without any AI (POC): nothing is sent to a model, the requirement is read by regex.
static bool HermesEnabled(IServiceProvider sp) => sp.GetRequiredService<IOptions<AppSettings>>().Value.Hermes.Enabled;
builder.Services.AddSingleton<IHermesClient>(sp => new LoggingHermesClient(
    HermesEnabled(sp) ? sp.GetRequiredService<HermesClient>() : new DisabledHermesClient(),
    sp.GetRequiredService<ILogger<LoggingHermesClient>>()));
// Prompts are loaded before the host starts (below): a missing required prompt stops startup (spec §9) and is logged.
builder.Services.AddSingleton(_ => PromptLibrary.Load(
    Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder), HermesTask.Spec, HermesTask.Statement, HermesTask.Invoice, HermesTask.Account));
builder.Services.AddSingleton<ISpecReader>(sp => HermesEnabled(sp)
    ? ActivatorUtilities.CreateInstance<HermesSpecReader>(sp)
    : new RegexSpecReader());
builder.Services.AddSingleton<IOcr>(sp =>
{
    var ocr = sp.GetRequiredService<IOptions<AppSettings>>().Value.Ocr;
    return ocr.Enabled ? new TesseractOcr(ocr.TessDataPath) : new DisabledOcr();
});
builder.Services.AddSingleton<StatementLlmExtractor>();
builder.Services.AddSingleton<StatementReader>();
builder.Services.AddSingleton<InvoiceExtractor>();
builder.Services.AddSingleton<AccountChooser>();
// T-601: the SDK on Windows, the simulated company with QuickBooks:Fake=true, otherwise nothing is ever sent.
builder.Services.AddSingleton(sp =>
{
    // Each SDK session step (connect, BeginSession, close) is logged: a hang there usually means a QuickBooks dialog.
    var sdkLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("QbAutopost.QuickBooks.QbSession");
    return QbConnection.Create(
        sp.GetRequiredService<IOptions<AppSettings>>().Value,
        sp.GetRequiredService<IHostEnvironment>(),
        step => sdkLog.LogInformation("QuickBooks SDK: {Step}", step));
});
// T-903 (FR-A-3): the COM probe only where the SDK can exist; everywhere else an honest "unavailable" answer.
builder.Services.AddSingleton<IQbSdkProbe>(sp =>
{
    var s = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    var mode = sp.GetRequiredService<QbConnection>().Mode;
    return mode == QbConnectionMode.Sdk && OperatingSystem.IsWindows()
        ? new QbSdkProbe(s.QuickBooks.QbXmlVersion)
        : new UnavailableQbSdkProbe(s.QuickBooks.QbXmlVersion, $"the QuickBooks gateway is {mode.ToString().ToLowerInvariant()}, not the Desktop SDK");
});
// One instance, registered twice: the interface for everything that posts, the concrete type for the connection test
// (FR-A-4 needs its own timeout and the lock-wait event). Two instances would mean two locks and two sessions.
builder.Services.AddSingleton(sp =>
{
    var qb = sp.GetRequiredService<IOptions<AppSettings>>().Value.QuickBooks;
    var busyTimeout = TimeSpan.FromSeconds(qb.BusyTimeoutSeconds);
    var logged = new LoggingQbGateway(
        sp.GetRequiredService<QbConnection>().Gateway, sp.GetRequiredService<ILogger<LoggingQbGateway>>(), busyTimeout);
    var gateway = new ResilientQbGateway(logged, new QbGatewayPolicy
    {
        BusyTimeout = busyTimeout,
        RetryDelay = TimeSpan.FromSeconds(qb.RetryDelaySeconds),
    });
    // T-904 (server defect 2): the log was silent while a call queued behind another, so a wait looked like a slow
    // QuickBooks. Below a second it is noise, so only a real wait is reported.
    var waitLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("QbAutopost.Core.Gateway.ResilientQbGateway");
    gateway.Waited += waited =>
    {
        if (waited > TimeSpan.FromSeconds(1))
        {
            waitLog.LogInformation("Waiting for QuickBooks: {WaitMs} ms behind another call", (long)waited.TotalMilliseconds);
        }
    };
    return gateway;
});
builder.Services.AddSingleton<IQbGateway>(sp => sp.GetRequiredService<ResilientQbGateway>());
builder.Services.AddSingleton<QbConnectionCheck>();
builder.Services.AddSingleton<CompanyFileValidator>();
builder.Services.AddSingleton(sp =>
{
    var s = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    return new PipelineOptions
    {
        CompanyName = s.Company.Name,
        RulesFile = s.Company.RulesFile,
        LedgerFile = s.Paths.Ledger,
        QbListsFile = s.Paths.QbLists,
        QbXmlVersion = s.QuickBooks.QbXmlVersion,
        DuplicateWindowDays = s.QuickBooks.DuplicateWindowDays,
        BackupFolder = s.QuickBooks.BackupFolder,
        BackupMaxAgeHours = s.QuickBooks.BackupMaxAgeHours,
    };
});
// T-907 (§6.1, §12): the bounds a direct request is read against. Core stays free of the settings types.
builder.Services.AddSingleton(sp =>
{
    var s = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    return new DirectPlanner(new DirectLimits
    {
        MaxRows = s.Api.MaxTransactionsPerRequest,
        MaxLineAmount = s.QuickBooks.MaxLineAmount,
        MaxAgeDays = s.QuickBooks.AllowedDateWindow.MaxAgeDays,
        MaxFutureDays = s.QuickBooks.AllowedDateWindow.MaxFutureDays,
    });
});
builder.Services.AddSingleton(sp =>
    new ApiBatchStore(sp.GetRequiredService<IOptions<AppSettings>>().Value.Paths.ApiBatches));
// T-909 (FR-A-12): beside the batches, because a key's whole purpose is to name the batch it produced.
builder.Services.AddSingleton(sp =>
{
    var s = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    return new IdempotencyStore(Path.Combine(s.Paths.ApiBatches, "idempotency.json"), s.Api.IdempotencyRetentionDays);
});
builder.Services.AddSingleton<DirectPostRunner>();
// T-914 (FR-A-7): the same StatementReader the job uses, so a validate and the job it predicts cannot disagree.
builder.Services.AddSingleton<FolderValidator>();
builder.Services.AddSingleton<JobPipeline>();
builder.Services.AddSingleton<QbListSync>();
builder.Services.AddSingleton<BatchUndo>();
builder.Services.AddSingleton<QbHealth>();
builder.Services.AddSingleton(sp =>
{
    var s = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    return new RulesEditor(s.Company.RulesFile, s.Paths.QbLists);
});
builder.Services.AddSingleton<IJobProcessor, JobRunner>();
builder.Services.AddSingleton<JobAdmission>();
builder.Services.AddSingleton<StartupRecovery>();
builder.Services.AddHostedService<JobWorker>();

var app = builder.Build();

try
{
    var settings = app.Services.GetRequiredService<IOptions<AppSettings>>().Value;
    StartupLog.Write(app.Logger, settings, app.Environment);

    RequireApiKey(app);

    // T-918 (Q-66): Development serves the documentation without a key, so an operator who has set the environment
    // wrongly on a real machine is told once, loudly, rather than discovering it from someone else's browser.
    if (settings.Api.Reference.Enabled && app.Environment.IsDevelopment())
    {
        app.Logger.LogWarning(
            "The API reference at {Path} and the OpenAPI document are served WITHOUT a key because the environment is Development. "
            + "It lists every route, scope and request shape. Set ASPNETCORE_ENVIRONMENT to Production on a real server.",
            ApiRoutes.V1Prefix + ApiRoutes.ReferencePath);
    }

    // T-911 (FR-A-14): refuse to serve a configuration that would expose keys and amounts, before a socket is opened.
    foreach (var warning in TransportGuard
                 .Require(settings.Api, settings.Paths, TransportGuard.CameFromFile(app.Configuration, "Api:Tls:PfxPassword"))
                 .Warnings)
    {
        app.Logger.LogWarning("{TransportWarning}", warning);
    }

    app.Services.GetRequiredService<PromptLibrary>();
    app.Logger.LogInformation("Hermes prompts loaded from {PromptFolder}", Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder));

    // With Ocr:Enabled the engine loads now, so missing language data stops startup rather than failing a job.
    app.Services.GetRequiredService<IOcr>();

    // QuickBooks:Fake outside Development/Testing stops startup; the chosen mode is logged once.
    var qbConnection = app.Services.GetRequiredService<QbConnection>();
    app.Logger.LogInformation("QuickBooks gateway: {Mode}", qbConnection.Mode);

    // Spec §6: before the worker starts (hosted services start in app.Run), no job may remain active.
    var recovered = app.Services.GetRequiredService<StartupRecovery>().Run();
    app.Logger.LogInformation("Startup recovery: {Count} interrupted job(s) closed", recovered);

    app.Lifetime.ApplicationStarted.Register(() =>
        app.Logger.LogInformation("QbAutopost ready, listening on {Urls}", string.Join(", ", app.Urls)));
    app.Lifetime.ApplicationStopping.Register(() =>
        app.Logger.LogInformation("QbAutopost stopping; a job still running is closed by startup recovery next time"));

    if (TransportGuard.UseHsts(settings.Api))
    {
        app.UseHsts();
    }

    // T-912 (api-v1 §2.6): first of all, so the id exists for the exception handler's problem detail and for the
    // request line, and so it is echoed even on an answer written by a middleware that returns early.
    app.UseMiddleware<RequestIdMiddleware>();
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseQbAutopostRequestLogging();
    // T-911 (FR-A-14): in front of the key check, so a 401 carries the same headers a 200 does.
    app.UseMiddleware<SecurityHeadersMiddleware>();
    // T-911 (FR-A-17): also in front of it — refusing a flood should not depend on who is sending it.
    app.UseMiddleware<RequestSizeMiddleware>();
    if (settings.Api.Cors.AllowedOrigins.Count > 0)
    {
        app.UseCors(CorsPolicyName);
    }

    // T-912 (FR-A-16): in front of the key check on purpose — a 403 is a refusal by an identified caller, and an
    // audit that sat behind the check would never see it (handoff trap 17). It records once the answer is known.
    app.UseMiddleware<AuditMiddleware>();
    app.UseMiddleware<ApiKeyMiddleware>();
    // T-911 (FR-A-15): behind the key check, so a partition can be the caller rather than whatever address they
    // happen to be calling from today.
    app.UseRateLimiter();
    // T-909 (FR-A-12): behind the key check, so an unauthenticated caller can neither claim an idempotency key nor
    // learn from a 409 which keys someone else has used.
    app.UseMiddleware<IdempotencyMiddleware>();
    // T-901 (api-v1 §3): the versioned surface. The flat paths of spec §6 are mapped as well while Api:LegacyRoutes
    // is true (api-v1 §9), so the POC package and the deploy scripts keep working until T-914 moves them.
    var v1 = app.MapGroup(ApiRoutes.V1Prefix);
    MapAll(v1);
    // T-913 (FR-A-18): the document and the Scalar page, on the versioned surface only and only when somebody asked
    // for them. Disabled means not mapped, so a route that could describe every way to move money simply is not there.
    if (settings.Api.Reference.Enabled)
    {
        v1.MapReferenceEndpoints(settings.Api.Reference, app.Environment);
    }

    if (settings.Api.LegacyRoutes)
    {
        var legacy = app.MapGroup(string.Empty);
        legacy.AddEndpointFilter(WarnOnLegacyRoute);
        MapAll(legacy);
    }

    app.Run();
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "QbAutopost stopped: {Error}", ex.Message);

    // Disposing the host flushes and closes the log file, so the reason is on disk before the process ends.
    await app.DisposeAsync();
    throw;
}

/// <summary>
/// api-v1 §9: a flat path still answers, but says so in the log at most once per route per hour, so the owner can see
/// which callers must move before the legacy routes are switched off (Q-53). The caller's identity is added at T-910.
/// </summary>
static async ValueTask<object?> WarnOnLegacyRoute(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var http = context.HttpContext;
    var pattern = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? string.Empty;
    if (http.RequestServices.GetRequiredService<LegacyRouteLog>().ShouldWarn(pattern))
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(LegacyRouteLog))
            .LogWarning("Legacy route {Path} used; use {Versioned} instead", pattern, ApiRoutes.V1Prefix + pattern);
    }

    return await next(context);
}

/// <summary>Every route of spec §6, mapped into <paramref name="routes"/> (the /api/v1 group, or the host itself).</summary>
static void MapAll(IEndpointRouteBuilder routes)
{
    routes.MapJobEndpoints();
    routes.MapHealthEndpoints();
    routes.MapQuickBooksEndpoints();
    routes.MapBatchEndpoints();
    routes.MapRulesEndpoints();
}

// Fail closed (FR-A-13): without either a usable caller list or a real shared key, every route but liveness and
// readiness would be open. Refusing to start is louder than serving an empty allow-list, and far louder than
// silently admitting nobody while an operator wonders why their integration broke.
static void RequireApiKey(WebApplication app)
{
    var api = app.Services.GetRequiredService<IOptions<AppSettings>>().Value.Api;
    var clients = app.Services.GetRequiredService<ApiClientStore>().Load().Clients;
    var usable = clients.Count(c => c.Enabled);
    var legacyUsable = api.AllowLegacyKey
                       && !string.IsNullOrWhiteSpace(api.ApiKey)
                       && (api.ApiKey != "change-me" || app.Environment.IsDevelopment());

    if (usable == 0 && !legacyUsable)
    {
        throw new InvalidOperationException(
            "No API caller can authenticate: clients.json has no enabled client and the shared Api:ApiKey is unusable. "
            + "Issue a key with scripts/new-api-client.ps1, or set QBAUTOPOST__Api__ApiKey with Api:AllowLegacyKey true.");
    }

    app.Logger.LogInformation(
        "API callers: {Clients} client(s) in {ClientsFile}; shared Api:ApiKey {Legacy}",
        usable,
        app.Services.GetRequiredService<ApiClientStore>().FilePath,
        legacyUsable ? "accepted (Api:AllowLegacyKey is true)" : "not accepted");

    if (legacyUsable && usable > 0)
    {
        // FR-A-13 expects the shared key to be retired once per-caller keys exist; it is all-scopes, so it
        // outranks every scope decision made in clients.json while it stays on.
        app.Logger.LogWarning(
            "The shared Api:ApiKey is still accepted and carries every scope. Set Api:AllowLegacyKey false once each caller has its own key.");
    }
}

/// <summary>Entry point; public so <c>WebApplicationFactory&lt;Program&gt;</c> can host it in tests.</summary>
public partial class Program
{
}
