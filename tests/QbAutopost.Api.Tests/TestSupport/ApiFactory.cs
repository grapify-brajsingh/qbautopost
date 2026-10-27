using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// The real host with fakes for QuickBooks and Hermes and every data file in a private temp folder (CLAUDE.md rule 1).
/// The host starts on first use, so files can be seeded into <see cref="Dir"/> before that.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key";
    public const string Company = "Tropicana Properties LLC";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public TempDir Dir { get; } = new();

    public FakeQbGateway Gateway { get; } = new();

    public FakeHermesClient Hermes { get; } = new();

    /// <summary>Rule 1: the host must never reach the real COM probe, not even on a developer's Windows box.</summary>
    public FakeQbSdkProbe SdkProbe { get; } = new();

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
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Api:ApiKey"] = ApiKey,
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
            ["Hermes:Enabled"] = "true",
            // Rule 1: even a real HermesClient built by mistake cannot reach a Hermes on this machine (.invalid never resolves).
            ["Hermes:BaseUrl"] = "http://hermes.invalid:8642",
            ["Hermes:ApiKey"] = "",
            ["QuickBooks:RetryDelaySeconds"] = "0.01",
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
