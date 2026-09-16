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
        WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));

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
