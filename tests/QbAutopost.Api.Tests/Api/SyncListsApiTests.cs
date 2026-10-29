using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>FR-15 <c>POST /qb/sync-lists</c> through the host and the fake company.</summary>
public sealed class SyncListsApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public SyncListsApiTests()
    {
        _client = _factory.CreateAuthorizedClient();
        var company = _factory.Gateway.Company;
        foreach (var name in new[] { "Chase Checking 4521", "Chase Sapphire 7788", "Repairs and Maintenance", "Office Supplies", "Utilities", "Automobile Expense", "Ask My Accountant" })
        {
            company.Accounts.Add(new QbAccount { Name = name, Type = "Expense" });
        }

        company.Vendors.Add("Home Depot");
        company.Vendors.Add("Amazon");
        company.Customers.Add("Palm Court Rentals LLC");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private string ListsFile => _factory.Dir.Combine("data", "qb-lists.json");

    private async Task<JsonElement> SyncOk()
    {
        using var response = await _client.PostAsync("/qb/sync-lists", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Should_ReturnListCounts_When_QuickBooksAnswers()
    {
        var body = await SyncOk();

        Assert.Equal(7, body.GetProperty("accounts").GetInt32());
        Assert.Equal(2, body.GetProperty("vendors").GetInt32());
        Assert.Equal(1, body.GetProperty("customers").GetInt32());
    }

    [Fact]
    public async Task Should_NameRulesAccountsQuickBooksLacks_When_Synced()
    {
        var body = await SyncOk();

        Assert.Equal(["Rental Income"], body.GetProperty("missingInRules").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Should_WriteQbListsFile_When_Synced()
    {
        await SyncOk();

        var lists = new QbListsStore(ListsFile).Load();
        Assert.Equal(7, lists.Accounts.Count);
        Assert.Equal(["Home Depot", "Amazon"], lists.Vendors);
        Assert.NotNull(lists.SyncedUtc);
    }

    [Fact]
    public async Task Should_Return503AndKeepFile_When_QuickBooksIsUnavailable()
    {
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("no session");

        using var response = await _client.PostAsync("/qb/sync-lists", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains("no session", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(ListsFile));
    }

    /// <summary>M14 §12: the v1 sync writes the same attempt line as the other triggers.</summary>
    [Fact]
    public async Task Should_LogTheAttemptLine_When_TheV1SyncSucceeds()
    {
        var logs = new CapturingLoggerProvider();
        using var host = LoggedHost(logs);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var response = await client.PostAsync("/api/v1/quickbooks/lists/sync", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(logs.Entries, e => e.Entry.Message.StartsWith("QuickBooks lists sync (api, by ", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Entry.Level);
        Assert.EndsWith("): ok, 7 accounts, 2 vendors, 1 customers", line.Entry.Message, StringComparison.Ordinal);
    }

    /// <summary>M14 §12: a failed v1 sync names its outcome and reason, and does not claim a file it may not use.</summary>
    [Fact]
    public async Task Should_LogTheFailedAttemptLine_When_TheV1SyncFails()
    {
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("no session");
        var logs = new CapturingLoggerProvider();
        using var host = LoggedHost(logs);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var response = await client.PostAsync("/api/v1/quickbooks/lists/sync", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var line = Assert.Single(logs.Entries, e => e.Entry.Message.StartsWith("QuickBooks lists sync (api, by ", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, line.Entry.Level);
        Assert.Contains("): failed (quickbooks-unavailable): ", line.Entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, e => e.Entry.Message.Contains("qb-lists.json unchanged", StringComparison.Ordinal));
    }

    private WebApplicationFactory<Program> LoggedHost(CapturingLoggerProvider logs) =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.AddProvider(logs)))));

    [Fact]
    public async Task Should_Return401_When_ApiKeyIsMissing()
    {
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.PostAsync("/qb/sync-lists", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Gateway.Requests);
    }

    /// <summary>M14 A-5 (§8): a v1 sync, by either spelling, is recorded with trigger <c>api</c> and the client's name.</summary>
    [Theory]
    [InlineData("/qb/sync-lists")]
    [InlineData("/api/v1/quickbooks/lists/sync")]
    public async Task Should_RecordTheSyncWithTriggerApi_When_TheV1RouteSyncs(string route)
    {
        using var response = await _client.PostAsync(route, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var run = _factory.Services.GetRequiredService<IQbListsSource>().LastAttempt;

        Assert.NotNull(run);
        Assert.Equal(ListSyncTrigger.Api, run.Trigger);
        Assert.Equal(ListSyncOutcome.Ok, run.Outcome);
        Assert.False(string.IsNullOrEmpty(run.RequestedBy));
    }

    [Fact]
    public async Task Should_SendOneReadOnlyMessageSet_When_Synced()
    {
        await SyncOk();

        Assert.Single(_factory.Gateway.Queries);
        Assert.Empty(_factory.Gateway.Writes);
    }
}
