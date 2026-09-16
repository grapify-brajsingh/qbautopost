using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Jobs;
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

var builder = WebApplication.CreateBuilder(args);

// Secrets may come from QBAUTOPOST__Section__Key environment variables (spec §12).
builder.Configuration.AddEnvironmentVariables(prefix: "QBAUTOPOST__");
builder.WebHost.UseUrls(builder.Configuration["Api:Bind"] ?? new ApiSettings().Bind);

var contentRoot = builder.Environment.ContentRootPath;
builder.Services.AddOptions<AppSettings>()
    .Bind(builder.Configuration)
    .PostConfigure(s => s.ResolvePaths(contentRoot));

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<IClock, SystemClock>();
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
builder.Services.AddHttpClient<IHermesClient, HermesClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
// Prompts load before the host starts: a missing required prompt stops startup (spec §9).
builder.Services.AddSingleton(PromptLibrary.Load(
    Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder), HermesTask.Spec, HermesTask.Statement, HermesTask.Invoice, HermesTask.Account));
builder.Services.AddSingleton<ISpecReader, HermesSpecReader>();
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
builder.Services.AddSingleton(sp => QbConnection.Create(
    sp.GetRequiredService<IOptions<AppSettings>>().Value, sp.GetRequiredService<IHostEnvironment>()));
builder.Services.AddSingleton<IQbGateway>(sp =>
{
    var qb = sp.GetRequiredService<IOptions<AppSettings>>().Value.QuickBooks;
    return new ResilientQbGateway(sp.GetRequiredService<QbConnection>().Gateway, new QbGatewayPolicy
    {
        BusyTimeout = TimeSpan.FromSeconds(qb.BusyTimeoutSeconds),
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

RequireApiKey(app);

// With Ocr:Enabled the engine loads now, so missing language data stops startup rather than failing a job.
app.Services.GetRequiredService<IOcr>();

// QuickBooks:Fake outside Development/Testing stops startup; the chosen mode is logged once.
var qbConnection = app.Services.GetRequiredService<QbConnection>();
app.Logger.LogInformation("QuickBooks gateway: {Mode}", qbConnection.Mode);

// Spec §6: before the worker starts (hosted services start in app.Run), no job may remain active.
app.Services.GetRequiredService<StartupRecovery>().Run();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapJobEndpoints();
app.MapHealthEndpoints();
app.MapQuickBooksEndpoints();
app.MapBatchEndpoints();
app.MapRulesEndpoints();

app.Run();

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
