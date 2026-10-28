using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>T-1012 (saved-requests spec 6, 6.2, 6.5, 5.3): the read routes of saved requests.</summary>
public sealed class ReadApiTests : IDisposable
{
    private const string Route = SavedRequestTestHost.Route;

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    [Fact]
    public async Task Should_ReturnDetailWithETagAndAllowedActions_When_EditorGets()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await editor.GetAsync($"{Route}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.ToString());
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal(["edit", "validate", "post", "discard"], body.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(1, body.GetProperty("rowVersion").GetInt32());
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal("n8n-good-2026-08", body.GetProperty("payload").GetProperty("reference").GetString());
        var source = body.GetProperty("source");
        Assert.Equal("api-client", source.GetProperty("kind").GetString());
        Assert.Equal(SavedRequestTestHost.N8nClientId, source.GetProperty("id").GetString());
        Assert.Equal(SavedRequestTestHost.N8nClientName, source.GetProperty("name").GetString());
        var createdBy = body.GetProperty("created").GetProperty("by");
        Assert.Equal("api-client:" + SavedRequestTestHost.N8nClientId, createdBy.GetProperty("id").GetString());
        Assert.Equal(SavedRequestTestHost.N8nClientName, createdBy.GetProperty("name").GetString());
        Assert.Equal("ok", body.GetProperty("validation").GetProperty("state").GetString());
        Assert.Empty(body.GetProperty("readErrors").EnumerateArray());
    }

    [Fact]
    public async Task Should_ShowUserNameAndEmail_When_ASignedInUserSaved()
    {
        using var editor = _host.User(DashboardRoles.Editor, out var userId);
        var (_, saved) = await _host.SaveAsync(SavedRequestTestHost.Fixture("n8n-good.json"), client: editor);

        var body = await GetJsonAsync(editor, $"{Route}/{saved.GetProperty("id").GetInt64()}");

        var by = body.GetProperty("created").GetProperty("by");
        Assert.Equal(userId, by.GetProperty("id").GetString());
        Assert.StartsWith("User ", by.GetProperty("name").GetString());
        Assert.EndsWith("@example.com", by.GetProperty("email").GetString());
        Assert.Equal("user", body.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal(userId, body.GetProperty("source").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Should_ReturnEmptyAllowedActions_When_ViewerGets()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var viewer = _host.User(DashboardRoles.Viewer);

        var body = await GetJsonAsync(viewer, $"{Route}/{id}");

        Assert.Empty(body.GetProperty("allowedActions").EnumerateArray());
    }

    [Fact]
    public async Task Should_ReturnExactBytes_When_GetOriginal()
    {
        var bytes = SavedRequestTestHost.FixtureBytes("n8n-unicode.json");
        using var n8n = _host.N8n();
        using var save = await n8n.PostAsync(Route, SavedRequestTestHost.Json(bytes));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var id = (await SavedRequestTestHost.BodyOf(save)).GetProperty("id").GetInt64();
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync($"{Route}/{id}/original");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("SR-")]
    [InlineData("SR-99999999999999999999")]
    [InlineData("99999999999999999999")]
    [InlineData("1.0")]
    [InlineData("sr-000001")]
    public async Task Should_Return404_When_IdIs(string id)
    {
        await _host.SaveFixtureAsync("n8n-good.json");
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync($"{Route}/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_Return404_When_IdIsUnknown()
    {
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync($"{Route}/42");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_ResolveSameRecord_When_IdIsPublicRef()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var viewer = _host.User(DashboardRoles.Viewer);

        var byRef = await GetJsonAsync(viewer, $"{Route}/SR-{id:000000}");

        Assert.Equal(id, byRef.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Should_FilterPageAndTotal_When_Listing()
    {
        await _host.SaveFixtureAsync("n8n-good.json");
        await _host.SaveFixtureAsync("n8n-dry-run.json");
        await _host.SaveFixtureAsync("n8n-control-total-wrong.json");
        using var viewer = _host.User(DashboardRoles.Viewer);

        var attention = await GetJsonAsync(viewer, $"{Route}?needsAttention=true&status=pending");
        var page = await GetJsonAsync(viewer, $"{Route}?pageSize=1&page=2");

        Assert.Equal(2, attention.GetProperty("total").GetInt32());
        Assert.All(attention.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("needsAttention").GetBoolean()));
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(1, page.GetProperty("pageSize").GetInt32());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        // Default sort is -created: page 2 of 1 is the second newest, the dry-run one.
        Assert.Equal("n8n-dry-run-2026-08", item.GetProperty("reference").GetString());
        Assert.False(item.TryGetProperty("payload", out _));
        Assert.False(item.TryGetProperty("readErrors", out _));
        Assert.False(item.GetProperty("validation").TryGetProperty("validation", out _));
    }

    [Theory]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=0")]
    [InlineData("status=bogus")]
    [InlineData("status=pending&status=nope")]
    [InlineData("needsAttention=maybe")]
    [InlineData("sort=amount")]
    [InlineData("createdFrom=27-10-2026")]
    public async Task Should_Return400_When_PageSizeOver100OrUnknownStatus(string query)
    {
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync($"{Route}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Should_ReturnCounts_When_Stats()
    {
        await _host.SaveFixtureAsync("n8n-good.json");
        await _host.SaveFixtureAsync("n8n-good.json");
        await _host.SaveFixtureAsync("n8n-unknown-kind.json");
        using var viewer = _host.User(DashboardRoles.Viewer);

        var stats = await GetJsonAsync(viewer, $"{Route}/stats");

        Assert.Equal(3, stats.GetProperty("total").GetInt32());
        Assert.Equal(3, stats.GetProperty("byStatus").GetProperty("pending").GetInt32());
        Assert.Equal(0, stats.GetProperty("byStatus").GetProperty("discarded").GetInt32());
        Assert.Equal(1, stats.GetProperty("needsAttention").GetInt32());
        Assert.Equal(1, stats.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task Should_ListVersionsNewestFirst_When_EditedTwice()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        await EditAsync(id, 1, "n8n-dry-run.json", "first fix");
        await EditAsync(id, 2, "n8n-control-total-wrong.json", "second fix");
        using var viewer = _host.User(DashboardRoles.Viewer);

        var versions = await GetJsonAsync(viewer, $"{Route}/{id}/versions");
        var one = await GetJsonAsync(viewer, $"{Route}/{id}/versions/1");

        Assert.Equal([3, 2, 1], versions.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("number").GetInt32()));
        Assert.Equal("second fix", versions.GetProperty("items")[0].GetProperty("note").GetString());
        Assert.Equal("n8n-good-2026-08", one.GetProperty("payload").GetProperty("reference").GetString());
    }

    [Fact]
    public async Task Should_Return404_When_VersionMissing()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync($"{Route}/{id}/versions/2");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_ListAttemptsNewestFirst_When_PostedTwice()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        var store = _host.Factory.Store;
        var first = await store.TryClaimAsync(id, null, "user-x", false, DateTime.UtcNow, CancellationToken.None);
        await store.CompleteAttemptAsync(
            first.AttemptId!.Value,
            new AttemptCompletion(AttemptOutcome.Busy, SavedRequestStatus.Pending, 409, null, null, null, null, DateTime.UtcNow),
            CancellationToken.None);
        var second = await store.TryClaimAsync(id, null, "user-x", false, DateTime.UtcNow, CancellationToken.None);
        using var viewer = _host.User(DashboardRoles.Viewer);

        var attempts = await GetJsonAsync(viewer, $"{Route}/{id}/attempts");
        var one = await GetJsonAsync(viewer, $"{Route}/{id}/attempts/{first.AttemptId}");

        var items = attempts.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([second.AttemptId!.Value, first.AttemptId.Value], items.Select(a => a.GetProperty("id").GetInt64()));
        Assert.Equal("in-progress", items[0].GetProperty("outcome").GetString());
        Assert.Equal("busy", one.GetProperty("outcome").GetString());
        Assert.Equal(409, one.GetProperty("httpStatus").GetInt32());
    }

    [Fact]
    public async Task Should_ListEventsNewestFirst_When_ARequestHasHistory()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        await EditAsync(id, 1, "n8n-dry-run.json", null);
        using var viewer = _host.User(DashboardRoles.Viewer);

        var events = await GetJsonAsync(viewer, $"{Route}/{id}/events");

        var items = events.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([SavedRequestEvents.Edited, SavedRequestEvents.Created], items.Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal("api-client:" + SavedRequestTestHost.N8nClientId, items[1].GetProperty("actor").GetProperty("id").GetString());
    }

    /// <summary>T-1013 maps the edit route; until then a version is appended through the intake it will call.</summary>
    private async Task EditAsync(long id, int rowVersion, string fixture, string? note)
    {
        var intake = _host.Factory.Services.GetRequiredService<SavedRequestIntake>();
        using var payload = JsonDocument.Parse(SavedRequestTestHost.Fixture(fixture));
        var caller = Caller.ForUser("user-editor", "Editor", "editor@example.com", DashboardRoles.Editor, false);
        var result = await intake.EditAsync(id, rowVersion, payload.RootElement, note, caller, CancellationToken.None);
        Assert.Equal(MutationKind.Ok, result.Kind);
    }
}
