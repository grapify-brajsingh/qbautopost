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
using QbAutopost.Core.Extract;
using QbAutopost.Core.Gateway;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Pipeline;

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

var contentRoot = builder.Environment.ContentRootPath;
builder.Services.AddOptions<AppSettings>()
    .Bind(builder.Configuration)
    .PostConfigure(s => s.ResolvePaths(contentRoot));

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<IClock, SystemClock>();
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
builder.Services.AddSingleton<IQbGateway>(sp =>
{
    var qb = sp.GetRequiredService<IOptions<AppSettings>>().Value.QuickBooks;
    var busyTimeout = TimeSpan.FromSeconds(qb.BusyTimeoutSeconds);
    var logged = new LoggingQbGateway(
        sp.GetRequiredService<QbConnection>().Gateway, sp.GetRequiredService<ILogger<LoggingQbGateway>>(), busyTimeout);
    return new ResilientQbGateway(logged, new QbGatewayPolicy
    {
        BusyTimeout = busyTimeout,
        RetryDelay = TimeSpan.FromSeconds(qb.RetryDelaySeconds),
    });
});
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

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseQbAutopostRequestLogging();
    app.UseMiddleware<ApiKeyMiddleware>();
    // T-901 (api-v1 §3): the versioned surface. The flat paths of spec §6 are mapped as well while Api:LegacyRoutes
    // is true (api-v1 §9), so the POC package and the deploy scripts keep working until T-914 moves them.
    MapAll(app.MapGroup(ApiRoutes.V1Prefix));
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

// Fail closed: without a real key every route except /health would be open.
static void RequireApiKey(WebApplication app)
{
    var key = app.Services.GetRequiredService<IOptions<AppSettings>>().Value.Api.ApiKey;
    if (string.IsNullOrWhiteSpace(key) || (key == "change-me" && !app.Environment.IsDevelopment()))
    {
        throw new InvalidOperationException(
            "Api:ApiKey is not set. Configure it in appsettings or the QBAUTOPOST__Api__ApiKey environment variable.");
    }
}

/// <summary>Entry point; public so <c>WebApplicationFactory&lt;Program&gt;</c> can host it in tests.</summary>
public partial class Program
{
}
