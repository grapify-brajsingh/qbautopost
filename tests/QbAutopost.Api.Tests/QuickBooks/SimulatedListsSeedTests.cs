using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.QuickBooks;

/// <summary>
/// T-1503 (M15 §4, A-8): with <c>QuickBooks:Fake=true</c> the simulated company starts with the accounts, vendors and
/// customers of <c>Paths:QbLists</c>, so a sync in Development returns real-looking names. A missing or unreadable file
/// leaves it empty with one Warning and never stops startup.
/// </summary>
public sealed class SimulatedListsSeedTests
{
    private const string Warning = "simulated QuickBooks company has no lists";

    [Fact]
    public async Task Should_SyncTheSeedCounts_When_TheListsFileHolds19Accounts15Vendors6Customers()
    {
        using var host = new SimulatedHost(seed: Fixtures.PathOf("qb-lists", "simulated-company.json"));

        var body = await SyncOkAsync(host);

        var counts = body.GetProperty("counts");
        Assert.Equal((19, 15, 6), (counts.GetProperty("accounts").GetInt32(), counts.GetProperty("vendors").GetInt32(), counts.GetProperty("customers").GetInt32()));
        var first = body.GetProperty("accounts")[0];
        Assert.Equal("Simulated Account 01", first.GetProperty("name").GetString());
        Assert.Equal("Bank", first.GetProperty("type").GetString());
        Assert.Contains("Simulated Vendor 15", body.GetProperty("vendors").EnumerateArray().Select(v => v.GetString()));
        Assert.DoesNotContain(host.Logs.Entries, e => e.Entry.Message.Contains(Warning, StringComparison.Ordinal));
    }

    [Fact]
    public void Should_StateTheSeedCountsInTheStartupLine_When_TheCompanyIsSeeded()
    {
        using var host = new SimulatedHost(seed: Fixtures.PathOf("qb-lists", "simulated-company.json"));
        using var client = host.CreateClient();

        var line = Assert.Single(host.Logs.Entries, e => e.Entry.Message.StartsWith("QuickBooks:", StringComparison.Ordinal)).Entry.Message;

        Assert.Contains($"simulated company: 19 accounts, 15 vendors, 6 customers from {host.ListsFile}", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SyncEmptyListsAndWarnOnce_When_TheListsFileIsMissing()
    {
        using var host = new SimulatedHost(seed: null);

        var body = await SyncOkAsync(host);

        Assert.Empty(body.GetProperty("accounts").EnumerateArray());
        Assert.Empty(body.GetProperty("vendors").EnumerateArray());
        Assert.Equal(0, body.GetProperty("counts").GetProperty("customers").GetInt32());
        var warning = Assert.Single(host.Logs.Entries, e => e.Entry.Message.Contains(Warning, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Entry.Level);
        Assert.Contains("simulated company: no lists", QuickBooksLine(host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_StartWithAnEmptyCompanyAndWarnOnce_When_TheListsFileIsUnreadable()
    {
        using var host = new SimulatedHost(seed: Fixtures.PathOf("qb-lists", "simulated-company.json"), corrupt: true);

        var body = await SyncOkAsync(host);

        Assert.Empty(body.GetProperty("accounts").EnumerateArray());
        var warning = Assert.Single(host.Logs.Entries, e => e.Entry.Message.Contains(Warning, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Entry.Level);
    }

    [Fact]
    public void Should_SeedTheCompanyBeforeItIsUsed_When_TheConnectionIsSimulated()
    {
        using var host = new SimulatedHost(seed: Fixtures.PathOf("qb-lists", "simulated-company.json"));
        using var client = host.CreateClient();

        var connection = host.Services.GetRequiredService<QbConnection>();

        Assert.Equal(QbConnectionMode.Simulated, connection.Mode);
        var company = Assert.IsType<SimulatedQbGateway>(connection.Gateway).Company;
        Assert.Equal((19, 15, 6), (company.Accounts.Count, company.Vendors.Count, company.Customers.Count));
        Assert.Equal(new SimulatedListsSeed(19, 15, 6, host.ListsFile), connection.SimulatedSeed);
    }

    [Fact]
    public void Should_NotSeed_When_TheConnectionIsATestDouble()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthorizedClient();

        Assert.Null(factory.Services.GetRequiredService<QbConnection>().SimulatedSeed);
    }

    private static string QuickBooksLine(SimulatedHost host) =>
        Assert.Single(host.Logs.Entries, e => e.Entry.Message.StartsWith("QuickBooks:", StringComparison.Ordinal)).Entry.Message;

    private static async Task<JsonElement> SyncOkAsync(SimulatedHost host)
    {
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        using var response = await client.PostAsync("/api/v2/quickbooks/lists/sync", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The real DI switch with <c>QuickBooks:Fake=true</c> (no test gateway); only Hermes is faked.</summary>
    private sealed class SimulatedHost : WebApplicationFactory<Program>
    {
        private readonly TempDir _dir = new();

        public SimulatedHost(string? seed, bool corrupt = false)
        {
            ListsFile = _dir.Combine("data", "qb-lists.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ListsFile)!);
            if (seed is not null)
            {
                File.Copy(seed, ListsFile);
            }

            if (corrupt)
            {
                File.WriteAllText(ListsFile, "{ \"accounts\": [ not json");
            }
        }

        public string ListsFile { get; }

        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:ApiKey"] = ApiFactory.ApiKey,
                ["Company:Name"] = ApiFactory.Company,
                ["Company:RulesFile"] = Fixtures.SampleRules,
                ["Paths:Ledger"] = _dir.Combine("data", "ledger.json"),
                ["Paths:QbLists"] = ListsFile,
                ["Paths:Logs"] = _dir.Combine("data", "logs"),
                ["Paths:JobIndex"] = _dir.Combine("data", "jobs.json"),
                ["OpenRouter:BaseUrl"] = "https://hermes.invalid:8642",
                ["QuickBooks:Fake"] = "true",
                ["QuickBooks:ListSync:IntervalMinutes"] = "0",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.AddProvider(Logs)));
                services.RemoveAll<IHermesClient>();
                services.AddSingleton<IHermesClient>(new FakeHermesClient());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _dir.Dispose();
            }
        }
    }
}
