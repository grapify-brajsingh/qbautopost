using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
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

    [Fact]
    public async Task Should_Return401_When_ApiKeyIsMissing()
    {
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.PostAsync("/qb/sync-lists", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_SendOneReadOnlyMessageSet_When_Synced()
    {
        await SyncOk();

        Assert.Single(_factory.Gateway.Queries);
        Assert.Empty(_factory.Gateway.Writes);
    }
}
