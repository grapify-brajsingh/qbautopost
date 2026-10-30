using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Endpoints.V2;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// M15 §3 (R-17), acceptance A-1…A-6: <c>GET /api/v2/quickbooks/lists/syncs</c>, the list sync history, in file mode (no
/// database): attempts since start, newest first, filtered and paged, with the same view as <c>lastSync</c>.
/// </summary>
public sealed class V2QbListSyncHistoryTests : IDisposable
{
    private const string Syncs = ApiRoutes.V2Prefix + "/quickbooks/lists/syncs";
    private const string Sync = ApiRoutes.V2Prefix + "/quickbooks/lists/sync";
    private static readonly DateTime T0 = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory = new();
    private readonly string _qbReadKey = ApiClientStore.NewKey();
    private readonly string _listsReadKey = ApiClientStore.NewKey();
    private readonly string _submitKey = ApiClientStore.NewKey();

    public V2QbListSyncHistoryTests()
    {
        var company = _factory.Gateway.Company;
        company.Accounts.Add(new QbAccount { Name = "Utilities", Type = "Expense" });
        company.Accounts.Add(new QbAccount { Name = "Chase Checking 4521", Type = "Bank" });
        company.Vendors.Add("Home Depot");
        company.Customers.Add("Palm Court Rentals LLC");

        var file = _factory.Dir.Combine("data", "clients.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        new ApiClientStore(file).Save(new ApiClientList
        {
            Clients = [Client("reader", _qbReadKey, ApiScopes.QbRead), Client("lists", _listsReadKey, ApiScopes.ListsRead), Client("n8n", _submitKey, ApiScopes.RequestsSubmit)],
        });
    }

    public void Dispose() => _factory.Dispose();

    // ---- A-1 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ListAttemptsNewestFirstWithTheEditorAndTheCurrentOk_When_ThreeAttemptsRecorded()
    {
        var (editorId, _) = await RecordThreeAsync();
        using var viewer = User(DashboardRoles.Viewer);

        var body = await GetOkAsync(viewer, Syncs);

        Assert.Equal(["items", "page", "pageSize", "total"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(25, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([3L, 2L, 1L], items.Select(i => i.GetProperty("id").GetInt64()));
        Assert.Equal(["schedule", "dashboard", "schedule"], items.Select(i => i.GetProperty("trigger").GetString()));
        Assert.Equal(["skipped", "ok", "ok"], items.Select(i => i.GetProperty("outcome").GetString()));
        Assert.Equal([false, true, false], items.Select(i => i.GetProperty("current").GetBoolean()));

        var dashboard = items[1];
        Assert.Equal(editorId, dashboard.GetProperty("requestedBy").GetProperty("id").GetString());
        Assert.Equal("User " + editorId["user-".Length..], dashboard.GetProperty("requestedBy").GetProperty("displayName").GetString());
        Assert.Equal(2, dashboard.GetProperty("counts").GetProperty("accounts").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("counts").GetProperty("vendors").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("counts").GetProperty("customers").GetInt32());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("requestedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("requestedBy").ValueKind);
    }

    [Fact]
    public async Task Should_ReadEveryItemAsLastSyncDoes_When_ComparedWithTheListsRoute()
    {
        await RecordThreeAsync();
        using var viewer = User(DashboardRoles.Viewer);

        var newest = (await GetOkAsync(viewer, Syncs)).GetProperty("items")[0];
        var last = (await GetOkAsync(viewer, ApiRoutes.V2Prefix + "/quickbooks/lists")).GetProperty("lastSync");

        foreach (var property in last.EnumerateObject())
        {
            Assert.Equal(property.Value.GetRawText(), newest.GetProperty(property.Name).GetRawText());
        }

        Assert.Equal(
            ["id", "trigger", "outcome", "reason", "startedUtc", "finishedUtc", "requestedBy", "message", "counts", "current"],
            newest.EnumerateObject().Select(p => p.Name));
        Assert.Equal(ListSyncMessages.For(Skipped(60)), newest.GetProperty("message").GetString());
        Assert.Equal(ListSyncReasons.QuickBooksNotOpen, newest.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Should_AnswerAnEmptyPage_When_NothingWasAttempted()
    {
        using var viewer = User(DashboardRoles.Viewer);

        var body = await GetOkAsync(viewer, Syncs);

        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    // ---- A-2 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ListOnlyTheEditorsRow_When_FilteredByDashboard()
    {
        await RecordThreeAsync();
        using var viewer = User(DashboardRoles.Viewer);

        var body = await GetOkAsync(viewer, Syncs + "?trigger=dashboard");

        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(2, Assert.Single(body.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Should_ListEveryNamedTrigger_When_FilteredByASetIgnoringCase()
    {
        await RecordThreeAsync();
        await RecordAsync(Ok(90, ListSyncTrigger.Import));
        using var viewer = User(DashboardRoles.Viewer);

        var body = await GetOkAsync(viewer, Syncs + "?trigger=Schedule,IMPORT");

        Assert.Equal(3, body.GetProperty("total").GetInt32());
        Assert.Equal([4L, 3L, 1L], body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()));
        Assert.True(body.GetProperty("items")[0].GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task Should_ListTheSkipWithNullCounts_When_FilteredBySkipped()
    {
        await RecordThreeAsync();
        using var viewer = User(DashboardRoles.Viewer);

        var body = await GetOkAsync(viewer, Syncs + "?outcome=skipped");

        Assert.Equal(1, body.GetProperty("total").GetInt32());
        var skip = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, skip.GetProperty("counts").ValueKind);
        Assert.False(skip.GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task Should_AnswerOnePageAndTheFilteredTotal_When_Paged()
    {
        await RecordThreeAsync();
        using var viewer = User(DashboardRoles.Viewer);

        var second = await GetOkAsync(viewer, Syncs + "?outcome=ok&page=2&pageSize=1");
        var past = await GetOkAsync(viewer, Syncs + "?page=3&pageSize=100");

        Assert.Equal(2, second.GetProperty("total").GetInt32());
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(1, second.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Empty(past.GetProperty("items").EnumerateArray());
        Assert.Equal(3, past.GetProperty("total").GetInt32());
    }

    // ---- A-3 ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DashboardRoles.Viewer)]
    [InlineData(DashboardRoles.Editor)]
    [InlineData(DashboardRoles.Admin)]
    public async Task Should_Answer200_When_AnyDashboardRoleReads(string role)
    {
        using var client = User(role);

        using var response = await client.GetAsync(Syncs);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer401_When_NoCredentialIsGiven()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(Syncs);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer403_When_AnApiKeyHoldsNeitherQbReadNorListsRead()
    {
        using var key = Key(_submitKey);

        using var response = await key.GetAsync(Syncs);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("qb")]
    [InlineData("lists")]
    public async Task Should_Answer200_When_AnApiKeyHoldsQbReadOrListsRead(string which)
    {
        using var key = Key(which == "qb" ? _qbReadKey : _listsReadKey);

        using var response = await key.GetAsync(Syncs);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Should_NeedListsReadAndUseTheReadBucket_When_TheHistoryIsAsked()
    {
        Assert.Equal(ApiScopes.ListsRead, ApiRoutes.ScopeFor("GET", new PathString(Syncs)));
        Assert.Equal(ApiScopes.ListsRead, ApiRoutes.ScopeFor("GET", new PathString("/API/V2/QuickBooks/Lists/Syncs")));
        Assert.Equal(ApiScopes.Admin, ApiRoutes.ScopeFor("POST", new PathString(Syncs)));
        Assert.False(ApiRoutes.IsV2ListSync("GET", new PathString(Syncs)));
        Assert.False(ApiRoutes.IsV2ListSync("POST", new PathString(Syncs)));
        Assert.Equal(RateLimitBucket.Default, RateLimitPolicy.BucketFor("GET", new PathString(Syncs)));
        Assert.Equal(RateLimitBucket.Post, RateLimitPolicy.BucketFor("POST", new PathString(Sync)));
    }

    [Fact]
    public async Task Should_NotServeTheHistory_When_AskedUnderV1()
    {
        using var key = Key(_qbReadKey);

        using var response = await key.GetAsync(ApiRoutes.V1Prefix + "/quickbooks/lists/syncs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- A-4 ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=0")]
    [InlineData("page=x")]
    [InlineData("trigger=nope")]
    [InlineData("trigger=")]
    [InlineData("trigger=dashboard,")]
    [InlineData("trigger=1")]
    [InlineData("outcome=done")]
    [InlineData("outcome=ok&outcome=failed")]
    [InlineData("foo=1")]
    public async Task Should_Answer400InvalidRequest_When_TheQueryIsNotUnderstood(string query)
    {
        using var viewer = User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync(Syncs + "?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid-request", problem.GetProperty("type").GetString());
        Assert.Equal("Invalid query", problem.GetProperty("title").GetString());
    }

    // ---- A-5 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_NeverCarryErrorMissingNamesOrLists_When_AnyItemIsRead()
    {
        await RecordAsync(Ok(0, ListSyncTrigger.Schedule) with { MissingInRules = ["Office Supplies"] });
        await RecordAsync(new ListSyncRun
        {
            Trigger = ListSyncTrigger.Schedule, StartedUtc = T0.AddMinutes(30), FinishedUtc = T0.AddMinutes(30),
            Outcome = ListSyncOutcome.Failed, Reason = ListSyncReasons.QuickBooksUnavailable, Error = "COM exception 0x8004 secret text",
        });
        using var viewer = User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync(Syncs);
        var text = await response.Content.ReadAsStringAsync();
        var items = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            Assert.False(item.TryGetProperty("error", out _));
            Assert.False(item.TryGetProperty("missingInRules", out _));
            Assert.False(item.TryGetProperty("lists", out _));
        }

        Assert.DoesNotContain("secret text", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Office Supplies", text, StringComparison.Ordinal);
    }

    // ---- A-6 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ListBothSyncsNewestFirst_When_TwoSyncsRanSinceStartWithoutADatabase()
    {
        using var editor = User(DashboardRoles.Editor);
        using var key = Key(ApiFactory.ApiKey);
        using (var first = await editor.PostAsync(Sync, null))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var second = await key.PostAsync(ApiRoutes.V1Prefix + "/quickbooks/lists/sync", null))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        var items = (await GetOkAsync(editor, Syncs)).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(["api", "dashboard"], items.Select(i => i.GetProperty("trigger").GetString()));
        Assert.True(items[0].GetProperty("id").GetInt64() > items[1].GetProperty("id").GetInt64());
        Assert.Equal([true, false], items.Select(i => i.GetProperty("current").GetBoolean()));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A-1: a scheduled ok, a dashboard ok by an editor (through the route), then a skipped scheduled attempt.</summary>
    private async Task<(string EditorId, string Token)> RecordThreeAsync()
    {
        await RecordAsync(Ok(0, ListSyncTrigger.Schedule));
        var token = _factory.Users.TokenFor(DashboardRoles.Editor);
        using (var editor = Bearer(token))
        using (var response = await editor.PostAsync(Sync, null))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await RecordAsync(Skipped(60));
        return (_factory.Users.IdOf(token), token);
    }

    private Task RecordAsync(ListSyncRun run) =>
        _factory.Services.GetRequiredService<IQbListsStore>().RecordAsync(
            run, run.Outcome == ListSyncOutcome.Ok ? QbLists.Empty with { SyncedUtc = run.FinishedUtc } : null, CancellationToken.None);

    private static ListSyncRun Ok(int minute, ListSyncTrigger trigger) => new()
    {
        Trigger = trigger, StartedUtc = T0.AddMinutes(minute), FinishedUtc = T0.AddMinutes(minute).AddSeconds(5),
        Outcome = ListSyncOutcome.Ok, Accounts = 0, Vendors = 0, Customers = 0,
    };

    private static ListSyncRun Skipped(int minute) => new()
    {
        Trigger = ListSyncTrigger.Schedule, StartedUtc = T0.AddMinutes(minute), FinishedUtc = T0.AddMinutes(minute),
        Outcome = ListSyncOutcome.Skipped, Reason = ListSyncReasons.QuickBooksNotOpen,
    };

    private static ApiClient Client(string id, string key, string scope)
    {
        var secret = ApiClientStore.NewSecret(key);
        return new ApiClient
        {
            Id = id, Name = id, KeyHash = secret.KeyHash, KeySalt = secret.KeySalt, Scopes = [scope], CreatedUtc = DateTime.UtcNow.AddDays(-1),
        };
    }

    private HttpClient User(string role) => Bearer(_factory.Users.TokenFor(role));

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient Key(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
