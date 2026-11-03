using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Companies;
using QbAutopost.Api.Tests.SavedRequests;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Models;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-1709 (M17 spec 4.7, 5.3, A-8, review focus 4): the QuickBooks lists and their sync history are per company;
/// <c>company=&lt;id&gt;</c> picks one on the lists, sync and history routes (the body may carry it for sync), and a
/// missing one is the default company, so the existing dashboard keeps working.
/// </summary>
public sealed class V2CompanyListsTests : IDisposable
{
    private const string Lists = "/api/v2/quickbooks/lists";
    private const string Sync = Lists + "/sync";
    private const string History = Lists + "/syncs";
    private const int DefaultId = 1;
    private const int TakomaId = 2;
    private const int SaId = 3;
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    // Companies:RootFolder must be absolute on the OS running the tests.
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\fake" : "/fake";
    private static readonly string TakomaFile = Path.Combine(Root, "Takoma.QBW");
    private static readonly string SaFile = Path.Combine(Root, "SA.QBW");

    private readonly ApiFactory _factory = new();
    private WebApplicationFactory<Program>? _host;

    public V2CompanyListsTests()
    {
        // The default company's file lacks "Automobile Expense" and "Kwik Fill"; Takoma's has them.
        var tropicana = _factory.Gateway.Company;
        tropicana.Accounts.Add(new QbAccount { Name = "Chase Checking 4521", Type = "Bank" });
        tropicana.Accounts.Add(new QbAccount { Name = "Utilities", Type = "Expense" });
        tropicana.Vendors.Add("Home Depot");

        var takoma = _factory.Gateway.CompanyFor(TakomaFile);
        takoma.Accounts.Add(new QbAccount { Name = "Chase Checking 4521", Type = "Bank" });
        takoma.Accounts.Add(new QbAccount { Name = "Automobile Expense", Type = "Expense" });
        takoma.Vendors.Add("Kwik Fill");
    }

    public void Dispose()
    {
        _host?.Dispose();
        _factory.Dispose();
    }

    private FakeQbGateway Gateway => _factory.Gateway;

    /// <summary>The host with a company root; bootstrap makes the default company 1 when it starts.</summary>
    private WebApplicationFactory<Program> Host
    {
        get
        {
            if (_host is null)
            {
                _host = _factory.WithSetting("Companies:RootFolder", Root);
                _host.CreateClient().Dispose();
            }

            return _host;
        }
    }

    private void SeedCompanies(CompanyAccess takomaAccess = CompanyAccess.Approved)
    {
        var host = Host;
        _factory.Companies.Add(CompanyOf(TakomaId, "Takoma Park Grocery Store Inc", TakomaFile, takomaAccess));
        _factory.Companies.Add(CompanyOf(SaId, "S & A Convenience Market LLC", SaFile, CompanyAccess.Approved));
        var folders = host.Services.GetRequiredService<CompanyDataFolder>();
        folders.Prepare(TakomaId);
        folders.Prepare(SaId);
        File.Copy(Fixtures.PathOf("multi-company", "takoma-rules.json"), folders.RulesFileOf(TakomaId), overwrite: true);
    }

    private static Company CompanyOf(int id, string name, string file, CompanyAccess access) =>
        new(id, null, null, name, file, [file], CompanyState.Active, access, T0, null, null, null, [], false, T0, T0, 1);

    private HttpClient Editor()
    {
        var client = Host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Users.TokenFor(DashboardRoles.Editor));
        return client;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, string? json = null)
    {
        using var client = Editor();
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await SavedRequestTestHost.BodyOf(response));
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    private Task<(HttpStatusCode Status, JsonElement Body)> SyncAsync(string query = "", string? json = null) =>
        SendAsync(HttpMethod.Post, Sync + query, json);

    private static List<string> AccountNames(JsonElement lists) =>
        lists.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToList();

    private static int Total(JsonElement page) => page.GetProperty("total").GetInt32();

    private static string Url(string route) => route switch
    {
        "lists" => Lists,
        "sync" => Sync,
        _ => History,
    };

    // ---- A-8: one company's sync leaves another's lists and history alone ------------------------------------------

    [Fact]
    public async Task Should_LeaveCompanyOneUntouched_When_CompanyTwoSyncs()
    {
        SeedCompanies();
        Assert.Equal(HttpStatusCode.OK, (await SyncAsync($"?company={DefaultId}")).Status);
        var (_, listsBefore) = await GetAsync($"{Lists}?company={DefaultId}");
        var (_, historyBefore) = await GetAsync($"{History}?company={DefaultId}");

        var (status, synced) = await SyncAsync($"?company={TakomaId}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Automobile Expense", AccountNames(synced));
        var (_, listsAfter) = await GetAsync($"{Lists}?company={DefaultId}");
        var (_, historyAfter) = await GetAsync($"{History}?company={DefaultId}");
        Assert.Equal(AccountNames(listsBefore), AccountNames(listsAfter));
        Assert.Equal(listsBefore.GetProperty("syncedUtc").GetString(), listsAfter.GetProperty("syncedUtc").GetString());
        Assert.Equal(Total(historyBefore), Total(historyAfter));
    }

    [Fact]
    public async Task Should_ShowTheCompanysOwnLists_When_ReadingWithCompany()
    {
        SeedCompanies();
        await SyncAsync($"?company={TakomaId}");

        var (status, lists) = await GetAsync($"{Lists}?company={TakomaId}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Chase Checking 4521", "Automobile Expense"], AccountNames(lists));
        Assert.Equal("ok", lists.GetProperty("lastSync").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Should_ShowEachCompanysOwnLists_When_ListingCompanies()
    {
        SeedCompanies();
        await SyncAsync($"?company={TakomaId}");

        var (status, body) = await GetAsync("/api/v2/companies");

        Assert.Equal(HttpStatusCode.OK, status);
        var items = body.GetProperty("items").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetInt32());
        Assert.Equal("ok", items[TakomaId].GetProperty("lists").GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, items[SaId].GetProperty("lists").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[DefaultId].GetProperty("lists").ValueKind);
    }

    [Fact]
    public async Task Should_AcceptCompanyInTheBody_When_Syncing()
    {
        SeedCompanies();

        var (status, synced) = await SyncAsync(json: $$"""{"company": {{TakomaId}}}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Automobile Expense", AccountNames(synced));
        Assert.All(Gateway.Targets, t => Assert.Equal(TakomaFile, t));
    }

    [Fact]
    public async Task Should_UseTheDefaultCompany_When_NoCompanyParameter()
    {
        SeedCompanies();

        var (status, synced) = await SyncAsync();
        var (_, lists) = await GetAsync($"{Lists}?company={DefaultId}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Chase Checking 4521", "Utilities"], AccountNames(synced));
        Assert.NotEmpty(Gateway.Targets);
        Assert.All(Gateway.Targets, Assert.Null);
        Assert.Equal(["Chase Checking 4521", "Utilities"], AccountNames(lists));
    }

    [Fact]
    public async Task Should_ValidateWithTheCompanysLists_When_StatementBelongsToCompanyTwo()
    {
        SeedCompanies();
        await SyncAsync($"?company={DefaultId}"); // lists without "Automobile Expense": a plan on them would hold the row
        await SyncAsync($"?company={TakomaId}");
        var (saved, body) = await SendAsync(
            HttpMethod.Post, "/api/v2/saved-requests", File.ReadAllText(Fixtures.PathOf("multi-company", "takoma-statement.json")));
        Assert.Equal(HttpStatusCode.Created, saved);
        var id = body.GetProperty("id").GetInt64();
        _factory.Store.SetCompany(id, TakomaId, CompanySource.Chosen);

        var (status, validated) = await SendAsync(HttpMethod.Post, $"/api/v2/saved-requests/{id}/validate");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", validated.GetProperty("validation").GetProperty("state").GetString());
    }

    // ---- readiness and the file QuickBooks has open ------------------------------------------------------------------

    [Fact]
    public async Task Should_Answer409NotReady_When_SyncingAnUnapprovedCompany()
    {
        SeedCompanies(takomaAccess: CompanyAccess.NeedsApproval);

        var (status, body) = await SyncAsync($"?company={TakomaId}");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("company-not-ready", body.GetProperty("type").GetString());
        Assert.Equal("needs-approval", body.GetProperty("access").GetString());
        Assert.Empty(Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer409OpenElsewhere_When_AnotherFileIsOpen()
    {
        SeedCompanies();
        Gateway.OpenCompanyFile = SaFile;

        var (status, body) = await SyncAsync($"?company={TakomaId}");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("company-file-open-elsewhere", body.GetProperty("type").GetString());
        Assert.Equal(TakomaFile, body.GetProperty("neededFile").GetString());
        var (_, history) = await GetAsync($"{History}?company={TakomaId}");
        var row = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal("skipped", row.GetProperty("outcome").GetString());
        Assert.Equal("company-file-open-elsewhere", row.GetProperty("reason").GetString());
        Assert.StartsWith("Another company file was open", row.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // ---- review focus 4: the company parameter -------------------------------------------------------------------------

    [Theory]
    [InlineData("lists", "abc")]
    [InlineData("lists", "0")]
    [InlineData("lists", "-5")]
    [InlineData("sync", "abc")]
    [InlineData("sync", "0")]
    [InlineData("sync", "-5")]
    [InlineData("syncs", "abc")]
    [InlineData("syncs", "0")]
    [InlineData("syncs", "-5")]
    public async Task Should_Answer400_When_CompanyParameterIsNotAnId(string route, string value)
    {
        SeedCompanies();
        var method = route == "sync" ? HttpMethod.Post : HttpMethod.Get;

        var (status, body) = await SendAsync(method, $"{Url(route)}?company={value}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", body.GetProperty("type").GetString());
        Assert.Empty(Gateway.Requests);
    }

    [Theory]
    [InlineData("lists")]
    [InlineData("sync")]
    [InlineData("syncs")]
    public async Task Should_Answer400_When_CompanyParameterIsRepeated(string route)
    {
        SeedCompanies();
        var method = route == "sync" ? HttpMethod.Post : HttpMethod.Get;

        var (status, body) = await SendAsync(method, $"{Url(route)}?company={DefaultId}&company={TakomaId}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Should_Answer400_When_CompanyIsGivenInQueryAndBody()
    {
        SeedCompanies();

        var (status, body) = await SyncAsync($"?company={TakomaId}", $$"""{"company": {{TakomaId}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", body.GetProperty("type").GetString());
        Assert.Empty(Gateway.Requests);
    }

    [Theory]
    [InlineData("""{"company": "2"}""")]
    [InlineData("""{"company": 0}""")]
    [InlineData("""{"company": 2.5}""")]
    [InlineData("""[2]""")]
    [InlineData("""{"company": 2""")]
    public async Task Should_Answer400_When_BodyCompanyIsNotAnId(string json)
    {
        SeedCompanies();

        var (status, body) = await SyncAsync(json: json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", body.GetProperty("type").GetString());
        Assert.Empty(Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer400_When_CompanyIsUnknown()
    {
        SeedCompanies();

        var (status, body) = await GetAsync($"{Lists}?company=99");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Should_Answer400WithoutASession_When_SyncingAnUnknownCompany()
    {
        SeedCompanies();

        var (status, body) = await SyncAsync("?company=99");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-request", body.GetProperty("type").GetString());
        Assert.Empty(Gateway.Requests);
    }

    [Fact]
    public async Task Should_AnswerAnEmptyPage_When_HistoryOfAnUnknownCompany()
    {
        SeedCompanies();
        await SyncAsync($"?company={DefaultId}");

        var (status, page) = await GetAsync($"{History}?company=99");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(0, Total(page));
    }
}
