using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1212b: the in-memory host with the upload switched on and the <b>real</b> <see cref="HermesClient"/> talking to
/// <see cref="FakeOpenRouterHandler"/> (no network, dashboard-api-m12 anti-goal 6), as production wraps it in
/// <see cref="LoggingHermesClient"/>. Every log event is captured before any scrubbing, so a leak would show.
/// </summary>
public sealed class ExtractionTestHost : IDisposable
{
    public const string Route = "/api/v2/statement-extractions";

    /// <summary>A marker key: it must reach the handler's Authorization header and nowhere else.</summary>
    public const string Key = "sk-or-v1-MARKERKEY-5521-do-not-log";

    private readonly Dictionary<string, string?> _settings;
    private WebApplicationFactory<Program>? _app;

    public ExtractionTestHost(IDictionary<string, string?>? settings = null)
    {
        _settings = new Dictionary<string, string?>
        {
            ["Extraction:Enabled"] = "true",
            ["OpenRouter:Enabled"] = "true",
            ["OpenRouter:ApiKey"] = Key,
        };
        foreach (var (k, v) in settings ?? new Dictionary<string, string?>())
        {
            _settings[k] = v;
        }
    }

    public SavedRequestTestHost Inner { get; } = new();

    public ApiFactory Factory => Inner.Factory;

    public FakeSavedRequestStore Store => Factory.Store;

    public FakeOpenRouterHandler Handler { get; } = new();

    public CapturingSink Logs { get; } = new();

    public WebApplicationFactory<Program> App => _app ??= Factory.WithWebHostBuilder(b =>
    {
        b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHermesClient>();
            services.AddSingleton<IHermesClient>(sp => new LoggingHermesClient(
                new HermesClient(new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan }, sp.GetRequiredService<HermesOptions>()),
                sp.GetRequiredService<ILogger<LoggingHermesClient>>()));
            var logger = new LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(Logs).CreateLogger();
            services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(logger, dispose: true));
        });
    });

    /// <summary>A signed-in user of <paramref name="role"/> (a new user each call) and their token.</summary>
    public string TokenFor(string role) => Factory.Users.TokenFor(role);

    public HttpClient ClientWithToken(string token)
    {
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient User(string role) => ClientWithToken(TokenFor(role));

    public HttpClient ApiKeyClient(string apiKey)
    {
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    public static MultipartFormDataContent Upload(byte[] bytes, string fileName = "chase-aug-2026.pdf", string part = "file")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, part, fileName);
        return content;
    }

    public static byte[] ChasePdf() => File.ReadAllBytes(Fixtures.PathOf("statements", "chase-checking-4521.pdf"));

    public static byte[] EncryptedPdf() => File.ReadAllBytes(Fixtures.PathOf("extractions", "encrypted.pdf"));

    /// <summary>A PDF built in memory: each page holds the given lines (Helvetica).</summary>
    public static byte[] Pdf(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(612, 792);
            var y = 740d;
            foreach (var line in lines)
            {
                page.AddText(line, 10, new PdfPoint(40, y), font);
                y -= 14;
            }
        }

        return builder.Build();
    }

    /// <summary>A page with plenty of readable text.</summary>
    public static string[] TextPage(string extra = "") =>
    [
        "SAMPLE BANK STATEMENT FOR TESTING ONLY",
        "Account 0000 1111 period September 2026",
        "09/01 Counter deposit 10.00 " + extra,
    ];

    public async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(HttpClient client, HttpContent content)
    {
        var response = await client.PostAsync(Route, content);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>Every captured line: rendered message, every property and any exception.</summary>
    public string LogText => Logs.Text;

    public void Dispose()
    {
        _app?.Dispose();
        Inner.Dispose();
    }

    public sealed class CapturingSink : ILogEventSink
    {
        private readonly object _gate = new();
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToList();
                }
            }
        }

        public string Text
        {
            get
            {
                var text = new StringBuilder();
                foreach (var e in Events)
                {
                    text.Append(e.RenderMessage()).Append(' ');
                    foreach (var (name, value) in e.Properties)
                    {
                        text.Append(name).Append('=').Append(value).Append(' ');
                    }

                    text.Append(e.Exception).Append('\n');
                }

                return text.ToString();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_gate)
            {
                _events.Add(logEvent);
            }
        }
    }
}
