using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using QbAutopost.Api.Auth;
using QbAutopost.Api.Database;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Data;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// The real host with fakes for QuickBooks and Hermes and every data file in a private temp folder (CLAUDE.md rule 1).
/// The host starts on first use, so files can be seeded into <see cref="Dir"/> before that.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key";
    public const string Company = "Tropicana Properties LLC";

    /// <summary>T-1009: signs the test access tokens (<see cref="FakeUsers"/>); 32+ bytes, as HS256 here requires.</summary>
    public const string SigningKey = "test-signing-key-0123456789abcdef-0123456789";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public TempDir Dir { get; } = new();

    public FakeQbGateway Gateway { get; } = new();

    public FakeHermesClient Hermes { get; } = new();

    /// <summary>Rule 1: the host must never reach the real COM probe, not even on a developer's Windows box.</summary>
    public FakeQbSdkProbe SdkProbe { get; } = new();

    /// <summary>
    /// Rule 1: no PostgreSQL either. The host gets this in-memory store, and a gate that starts <c>Ready</c>; a test
    /// moves the gate with <c>Gate.Set(...)</c>, or changes what an explicit refresh finds with <see cref="Schema"/>.
    /// </summary>
    public FakeSavedRequestStore Store { get; }

    /// <summary>
    /// The fake gate; with <see cref="WithPostgres"/>, the host's real gate (resolving it starts the host).
    /// </summary>
    public DatabaseGate Gate => _postgres is null ? _fakeGate : Services.GetRequiredService<DatabaseGate>();

    /// <summary>T-1009: dashboard users for Bearer requests, in place of UserManager (rule 1: no PostgreSQL).</summary>
    public FakeUsers Users { get; } = new();

    /// <summary>What the gate's schema check reports when something refreshes it.</summary>
    public SchemaStatus Schema { get; set; } = new(true, true, [], null);

    /// <summary>M10 review: how many times the fake gate's schema check ran.</summary>
    public int SchemaChecks => Volatile.Read(ref _schemaChecks);

    /// <summary>M10 review: moves the fake gate's clock, so a test can make its last check older than its throttle.</summary>
    public TimeSpan GateClockOffset { get; set; }

    private readonly DatabaseGate _fakeGate;

    private int _schemaChecks;

    /// <summary>R14: a PostgreSQL connection string, or null for the usual in-memory host.</summary>
    private readonly string? _postgres;

    public ApiFactory()
        : this(null)
    {
    }

    private ApiFactory(string? postgres, FakeSavedRequestStore? store = null)
    {
        _postgres = postgres;
        Store = store ?? new FakeSavedRequestStore();

        // Not forced: the host's first start-up check makes it Ready, and runs recovery on Store first, as production does.
        _fakeGate = new DatabaseGate(
            _ =>
            {
                Interlocked.Increment(ref _schemaChecks);
                return Task.FromResult(Schema);
            },
            true,
            NullLogger<DatabaseGate>.Instance,
            ct => Store.RecoverInFlightAsync(DateTime.UtcNow, ct),
            () => DateTime.UtcNow + GateClockOffset);
    }

    /// <summary>
    /// T-1014: a fresh host (its own folder, gate and process state) over an existing <paramref name="store"/>, as a
    /// restarted service finds the database: its start-up check runs in-flight recovery on that store first.
    /// </summary>
    public static ApiFactory Restarted(FakeSavedRequestStore store) => new(null, store);

    /// <summary>
    /// R14 (T-1010): the real host against a real PostgreSQL (Testcontainers), with the production wiring of
    /// <c>Program</c>: <c>AddQbAutopostData</c>, the EF store, the real <see cref="DatabaseGate"/> and its schema check,
    /// <c>InFlightRecovery</c>, Identity over the pooled context, and the real <see cref="UserGate"/>. Only QuickBooks
    /// and Hermes stay fake. The database must already be migrated: the app never migrates (spec 5.5).
    /// <see cref="Store"/> and <see cref="Users"/> are not used by such a host.
    /// </summary>
    public static ApiFactory WithPostgres(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new ApiFactory(connectionString);
    }

    public string LedgerFile => Dir.Combine("data", "ledger.json");

    public string JobIndexFile => Dir.Combine("data", "jobs.json");

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    /// <summary>A host with one setting overridden (applied after the defaults above).</summary>
    public WebApplicationFactory<Program> WithSetting(string key, string? value) =>
        WithSettings(new Dictionary<string, string?> { [key] = value });

    /// <summary>A host with several settings overridden — T-911's limits need more than one at a time.</summary>
    public WebApplicationFactory<Program> WithSettings(IDictionary<string, string?> settings) =>
        WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (_postgres is not null)
        {
            // UseSetting, not the collection below: Program reads it while it registers services.
            builder.UseSetting("Database:ConnectionString", _postgres);
        }

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Api:ApiKey"] = ApiKey,
            ["Auth:Jwt:SigningKey"] = SigningKey,
            ["DryRunDefault"] = "true",
            ["Company:Name"] = Company,
            ["Company:RulesFile"] = Fixtures.SampleRules,
            ["Paths:Ledger"] = LedgerFile,
            ["Paths:QbLists"] = Dir.Combine("data", "qb-lists.json"),
            ["Paths:Logs"] = Dir.Combine("data", "logs"),
            ["Paths:JobIndex"] = JobIndexFile,
            ["Paths:Clients"] = Dir.Combine("data", "clients.json"),
            // Rule 1 again: the shipped appsettings.json names a real machine folder, and the test host inherits it.
            // Without this line a posting test writes its batch evidence to C:\qb-autopost on a developer's box.
            ["Paths:ApiBatches"] = Dir.Combine("data", "api-batches"),
            // T-911 (handoff trap 14): the limits are meant for the internet, and this suite sends far more than 120
            // requests a minute to these same routes. Tests that need the limiter turn it on themselves; that it
            // ships *on* is asserted by RateLimitTests.Should_ShipEnabled_When_NobodyConfiguresIt.
            ["Api:RateLimits:Enabled"] = "false",
            // The shipped appsettings.json now ships Hermes OFF (T-806 POC mode), and the test host inherits it
            // (trap 4). Most of this suite exercises the Hermes path against FakeHermesClient, so the flag is pinned
            // on here, and PocModeApiTests turns it back off for the one host that tests the disabled mode.
            ["OpenRouter:Enabled"] = "true",
            // Rule 1: even a real HermesClient built by mistake cannot reach a Hermes on this machine (.invalid never resolves).
            ["OpenRouter:BaseUrl"] = "https://hermes.invalid:8642",
            ["OpenRouter:ApiKey"] = "",
            ["QuickBooks:RetryDelaySeconds"] = "0.01",
            // M14 §5: a long-lived test host must not sync on its own two minutes in (it would query the fake
            // gateway and rewrite qb-lists.json under a running test). ScheduledListSyncTests drive the schedule directly.
            ["QuickBooks:ListSync:IntervalMinutes"] = "0",
        }));
        builder.ConfigureTestServices(services =>
        {
            // The fake replaces the raw connection; the host still wraps it in ResilientQbGateway (busy timeout, retry).
            services.RemoveAll<QbConnection>();
            services.AddSingleton(new QbConnection(Gateway, QbConnectionMode.Test));
            services.RemoveAll<IHermesClient>();
            services.AddSingleton<IHermesClient>(Hermes);
            // Without this the host would pick the real COM probe on a Windows dev box (CLAUDE.md rule 1).
            services.RemoveAll<IQbSdkProbe>();
            services.AddSingleton<IQbSdkProbe>(SdkProbe);
            if (_postgres is not null)
            {
                // R14: the real store, gate and user gate, exactly as Program registered them.
                return;
            }

            services.RemoveAll<ISavedRequestStore>();
            services.AddSingleton<ISavedRequestStore>(Store);
            services.RemoveAll<DatabaseGate>();
            services.AddSingleton(_fakeGate);
            services.RemoveAll<IUserGate>();
            services.AddSingleton<IUserGate>(Users);
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IUserDirectory>(Users);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Dir.Dispose();
        }
    }
}

public static class ApiClientExtensions
{
    public static Task<HttpResponseMessage> PostJobAsync(this HttpClient client, string? folder, bool? dryRun = null, bool? force = null) =>
        client.PostAsJsonAsync("/jobs", new { folder, dryRun, force }, ApiFactory.Json);

    public static async Task<JobView> GetJobAsync(this HttpClient client, string jobId)
    {
        using var response = await client.GetAsync($"/jobs/{jobId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobView>(ApiFactory.Json))!;
    }

    /// <summary>Polls until the worker no longer owns the job.</summary>
    public static async Task<JobView> WaitForJobAsync(this HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var view = await client.GetJobAsync(jobId);
            if (!JobStatusRules.IsActive(view.Status) || DateTime.UtcNow > deadline)
            {
                return view;
            }

            await Task.Delay(20);
        }
    }

    public static async Task<JobView> RunToEndAsync(this HttpClient client, string folder, bool dryRun = true)
    {
        using var response = await client.PostJobAsync(folder, dryRun);
        response.EnsureSuccessStatusCode();
        return await client.WaitForJobAsync(FolderReaderJobId(folder));
    }

    public static async Task<JsonElement> ReadProblemAsync(this HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string FolderReaderJobId(string folder) => FolderReader.JobIdOf(folder);
}
