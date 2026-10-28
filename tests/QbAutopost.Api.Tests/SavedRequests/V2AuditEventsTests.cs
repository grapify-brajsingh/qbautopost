using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Endpoints.V2;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests.Audit;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1114 (dashboard v2 spec 8): <c>GET /api/v2/audit-events</c> and <c>/audit-events.csv</c> over the fake store, whose
/// feed is built with the same <see cref="AuditEventMapper"/> table as the view.
/// </summary>
public sealed class V2AuditEventsTests : IDisposable
{
    private const string Feed = "/api/v2/audit-events";
    private const string Csv = "/api/v2/audit-events.csv";
    private const string V2Save = "/api/v2/saved-requests";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private async Task<long> SaveAsync(string fixture)
    {
        using var client = _host.N8n();
        var body = File.ReadAllText(Fixtures.PathOf("saved-requests-v2", fixture));
        using var response = await client.PostAsync(V2Save, SavedRequestTestHost.Json(body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SavedRequestTestHost.BodyOf(response)).GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> GetAsync(string role, string path)
    {
        using var client = _host.User(role);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    private void SeedUserEvent(string actor, string kind, int minutesAgo = 1) =>
        Store.AddUserAudit(new UserAuditEntry(0, actor, actor, kind, null, DateTime.UtcNow.AddMinutes(-minutesAgo)));

    [Fact]
    public async Task Should_ReturnFeedShape_When_Read()
    {
        var id = await SaveAsync("with-email.json");

        var body = await GetAsync(DashboardRoles.Viewer, Feed);

        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(50, body.GetProperty("pageSize").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(items.Count, body.GetProperty("total").GetInt32());
        var received = items.Single(e => e.GetProperty("action").GetString() == "received");
        foreach (var name in new[] { "id", "atUtc", "actor", "action", "request", "row", "field", "old", "new", "versionNumber", "detail" })
        {
            Assert.True(received.TryGetProperty(name, out _), $"missing {name}");
        }

        Assert.StartsWith("e", received.GetProperty("id").GetString());
        Assert.EndsWith("Z", received.GetProperty("atUtc").GetString());
        var actor = received.GetProperty("actor");
        Assert.Equal($"api-client:{SavedRequestTestHost.N8nClientId}", actor.GetProperty("id").GetString());
        Assert.Equal(SavedRequestTestHost.N8nClientName, actor.GetProperty("name").GetString());
        Assert.Equal("api-client", actor.GetProperty("kind").GetString());
        var request = received.GetProperty("request");
        Assert.Equal(id, request.GetProperty("id").GetInt64());
        Assert.Equal($"SR-{id:000000}", request.GetProperty("publicRef").GetString());
        Assert.Equal(JsonValueKind.String, request.GetProperty("pushId").ValueKind);
        Assert.Equal(JsonValueKind.String, request.GetProperty("emailSubject").ValueKind);
        Assert.True(request.TryGetProperty("reference", out _));
        Assert.Equal(JsonValueKind.Null, received.GetProperty("row").ValueKind);
        var times = items.Select(e => e.GetProperty("atUtc").GetDateTime()).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
    }

    [Fact]
    public async Task Should_HideUserEvents_When_CallerLacksUsersManage()
    {
        await SaveAsync("with-email.json");
        SeedUserEvent("user-1", "login");

        var viewer = await GetAsync(DashboardRoles.Viewer, Feed);
        var admin = await GetAsync(DashboardRoles.Admin, Feed);

        Assert.DoesNotContain(viewer.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString()!.StartsWith("user-"));
        Assert.Equal(viewer.GetProperty("total").GetInt32() + 1, admin.GetProperty("total").GetInt32());
        var signIn = admin.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("action").GetString() == "user-signed-in");
        Assert.Equal(JsonValueKind.Null, signIn.GetProperty("request").ValueKind);
    }

    [Theory]
    [InlineData("from=2026-10-01T00:00:00")]
    [InlineData("to=2026-10-01")]
    [InlineData("from=yesterday")]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=201")]
    [InlineData("action=not-an-action")]
    [InlineData("requestId=abc")]
    [InlineData("pushId=123")]
    [InlineData("from=2026-10-01T00:00:00Z&from=2026-10-02T00:00:00Z")]
    public async Task Should_Answer400_When_FromHasNoOffset(string query)
    {
        using var client = _host.User(DashboardRoles.Viewer);

        using var feed = await client.GetAsync($"{Feed}?{query}");
        using var csv = await client.GetAsync($"{Csv}?{query}");

        // The CSV has no paging, so page and pageSize are not read there.
        var checks = query.StartsWith("page") ? new[] { feed } : new[] { feed, csv };
        foreach (var response in checks)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid-request", (await SavedRequestTestHost.BodyOf(response)).GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Should_AcceptOffsetInstant_When_FromIsGiven()
    {
        await SaveAsync("with-email.json");

        var all = await GetAsync(DashboardRoles.Viewer, $"{Feed}?from=2020-01-01T00:00:00%2B02:00");
        var none = await GetAsync(DashboardRoles.Viewer, $"{Feed}?from=2099-01-01T00:00:00Z");

        Assert.True(all.GetProperty("total").GetInt32() > 0);
        Assert.Equal(0, none.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Should_Answer413_When_CsvOverCap()
    {
        for (var i = 0; i < AuditCsv.MaxRows + 1; i++)
        {
            SeedUserEvent("user-1", "login");
        }

        using var admin = _host.User(DashboardRoles.Admin);
        using var response = await admin.GetAsync(Csv);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("too-many-rows", (await SavedRequestTestHost.BodyOf(response)).GetProperty("type").GetString());

        // A caller who cannot see user events does not count them.
        using var viewer = _host.User(DashboardRoles.Viewer);
        using var visible = await viewer.GetAsync(Csv);
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    [InlineData("\rx", "\"'\rx\"")]
    public void Should_PrefixQuote_When_ValueStartsWithFormulaChar(string value, string expected) =>
        Assert.Equal(expected, AuditCsv.Escape(value));

    [Theory]
    [InlineData(null, "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("a=b", "a=b")]
    public void Should_EscapeRfc4180_When_ValueNeedsQuoting(string? value, string expected) =>
        Assert.Equal(expected, AuditCsv.Escape(value));

    [Fact]
    public async Task Should_BoundCsvToStartTime_When_RowsArriveDuringExport()
    {
        SeedUserEvent("user-1", "login");
        SeedUserEvent("user-1", "login-failed", minutesAgo: -60); // stamped after the export starts

        using var admin = _host.User(DashboardRoles.Admin);
        using var response = await admin.GetAsync(Csv);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(",user-signed-in,", text);
        Assert.DoesNotContain(",user-sign-in-failed,", text);
    }

    [Fact]
    public async Task Should_WriteBomAndHeader_When_CsvRequested()
    {
        var id = await SaveAsync("with-email.json");
        using var client = _host.User(DashboardRoles.Viewer);

        using var response = await client.GetAsync(Csv);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("time_utc,actor,action,reference,push_id,sr_ref,row,field,old,new", lines[0]);
        var received = lines.Single(l => l.Contains(",received,"));
        Assert.Contains(SavedRequestTestHost.N8nClientName, received);
        Assert.Contains($"SR-{id:000000}", received);
    }

    [Fact]
    public async Task Should_FilterByActor_When_UserPageAsks()
    {
        SeedUserEvent("user-a", "login", 3);
        SeedUserEvent("user-a", "login-failed", 2);
        SeedUserEvent("user-b", "login", 1);

        var body = await GetAsync(DashboardRoles.Admin, $"{Feed}?actor=user-a");

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.All(items, e => Assert.Equal("user-a", e.GetProperty("actor").GetProperty("id").GetString()));
        Assert.Equal(["user-sign-in-failed", "user-signed-in"], items.Select(e => e.GetProperty("action").GetString()));

        var onlyFailed = await GetAsync(DashboardRoles.Admin, $"{Feed}?actor=user-a&action=user-sign-in-failed");
        Assert.Equal(1, onlyFailed.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Should_MatchHistoryCount_When_FeedFilteredByRequest()
    {
        var id = await SaveAsync("with-email.json");
        var other = await SaveAsync("three-rows-with-email.json");
        using (var editor = _host.User(DashboardRoles.Editor))
        {
            using var validated = await editor.PostAsync($"{V2Save}/{id}/validate", null);
            Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        }

        var detail = await GetAsync(DashboardRoles.Viewer, $"{V2Save}/{id}");
        var feed = await GetAsync(DashboardRoles.Viewer, $"{Feed}?requestId={id}&pageSize=200");
        var byRef = await GetAsync(DashboardRoles.Viewer, $"{Feed}?requestId=SR-{id:000000}");

        Assert.True(detail.GetProperty("historyCount").GetInt32() > 1);
        Assert.Equal(detail.GetProperty("historyCount").GetInt32(), feed.GetProperty("total").GetInt32());
        Assert.Equal(feed.GetProperty("total").GetInt32(), byRef.GetProperty("total").GetInt32());
        Assert.All(
            feed.GetProperty("items").EnumerateArray(),
            e => Assert.Equal(id, e.GetProperty("request").GetProperty("id").GetInt64()));
        Assert.NotEqual(id, other);
    }

    [Fact]
    public async Task Should_NameSystemAndAnonymousActors_When_FeedRead()
    {
        Store.AddUserAudit(new UserAuditEntry(0, null, "system", "login", null, DateTime.UtcNow.AddMinutes(-2)));
        Store.AddUserAudit(new UserAuditEntry(0, null, "anonymous", "login-failed", null, DateTime.UtcNow.AddMinutes(-1)));

        var body = await GetAsync(DashboardRoles.Admin, Feed);

        var actors = body.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("actor")).ToList();
        Assert.Contains(actors, a => a.GetProperty("id").GetString() == "system"
            && a.GetProperty("name").GetString() == "system" && a.GetProperty("kind").GetString() == "system");
        Assert.Contains(actors, a => a.GetProperty("id").GetString() == "anonymous"
            && a.GetProperty("name").GetString() == "anonymous" && a.GetProperty("kind").GetString() == "anonymous");
    }

    [Fact]
    public async Task Should_RequireRequestsRead_When_FeedCalledWithoutIt()
    {
        using var anonymous = _host.Factory.CreateClient();
        using var n8n = _host.N8n();

        using var none = await anonymous.GetAsync(Feed);
        using var submitOnly = await n8n.GetAsync(Csv);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, submitOnly.StatusCode);
    }
}
