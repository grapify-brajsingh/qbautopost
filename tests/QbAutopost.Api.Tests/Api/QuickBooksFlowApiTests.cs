using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>T-608: the T-609 server sequence end to end against fakes (spec §15).</summary>
public sealed class QuickBooksFlowApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Should_RunHealthSyncDryRunPostAndUndo_When_DoneInServerOrder()
    {
        using var client = _factory.CreateAuthorizedClient();
        foreach (var name in new[] { "Chase Checking 4521", "Chase Sapphire 7788", "Repairs and Maintenance", "Office Supplies", "Utilities", "Automobile Expense", "Ask My Accountant", "Rental Income" })
        {
            _factory.Gateway.Company.Accounts.Add(new QbAccount { Name = name });
        }

        using (var health = await client.GetAsync("/health/quickbooks"))
        {
            Assert.True((await Json(health, HttpStatusCode.OK)).GetProperty("ok").GetBoolean());
        }

        using (var sync = await client.PostAsync("/qb/sync-lists", null))
        {
            Assert.Equal(0, (await Json(sync, HttpStatusCode.OK)).GetProperty("missingInRules").GetArrayLength());
        }

        var folder = _factory.Dir.CopySampleJob();
        var ready = await client.RunToEndAsync(folder);
        Assert.Equal(JobStatus.Ready, ready.Status);

        using (var post = await client.PostAsync("/jobs/2026-08-tropicana/post", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        }

        var posted = await client.WaitForJobAsync("2026-08-tropicana");
        Assert.Equal((JobStatus.Partial, 8), (posted.Status, posted.Counts.Posted));
        Assert.Equal(8, _factory.Gateway.Company.Transactions.Count);

        using (var undo = await client.PostAsync($"/batches/{Uri.EscapeDataString(posted.BatchId!)}/undo", null))
        {
            Assert.Equal(8, (await Json(undo, HttpStatusCode.OK)).GetProperty("deleted").GetInt32());
        }

        Assert.Empty(_factory.Gateway.Company.Transactions);
        Assert.Equal(JobStatus.Undone, (await client.GetJobAsync("2026-08-tropicana")).Status);
    }

    [Fact]
    public async Task Should_PostThroughSimulatedCompany_When_FakeSettingIsOnWithoutTestGateway()
    {
        using var host = new SimulatedHostFactory();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        var folder = host.Dir.CopySampleJob();

        var view = await client.RunToEndAsync(folder, dryRun: false);

        Assert.Equal(QbConnectionMode.Simulated, host.Services.GetRequiredService<QbConnection>().Mode);
        Assert.Equal((JobStatus.Partial, 8), (view.Status, view.Counts.Posted));
        Assert.All(view.Posted, p => Assert.StartsWith("SIM-", p.TxnId, StringComparison.Ordinal));
        Assert.Equal(8, new LedgerStore(host.Dir.Combine("data", "ledger.json")).Load().Posted.Count);
    }

    /// <summary>The real DI switch with <c>QuickBooks:Fake=true</c>; only Hermes is faked (no test gateway).</summary>
    private sealed class SimulatedHostFactory : WebApplicationFactory<Program>
    {
        public TempDir Dir { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:ApiKey"] = ApiFactory.ApiKey,
                ["Company:Name"] = ApiFactory.Company,
                ["Company:RulesFile"] = Fixtures.SampleRules,
                ["Paths:Ledger"] = Dir.Combine("data", "ledger.json"),
                ["Paths:QbLists"] = Dir.Combine("data", "qb-lists.json"),
                ["Paths:Logs"] = Dir.Combine("data", "logs"),
                ["Paths:JobIndex"] = Dir.Combine("data", "jobs.json"),
                ["Hermes:BaseUrl"] = "http://hermes.invalid:8642",
                ["QuickBooks:Fake"] = "true",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHermesClient>();
                services.AddSingleton<IHermesClient>(new FakeHermesClient());
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
}
