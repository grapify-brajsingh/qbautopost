using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>T-1013 (saved-requests spec 7.1, 11.1): discard (editor) and restore (admin).</summary>
public sealed class LifecycleApiTests : IDisposable
{
    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private async Task<SavedRequest> StoredAsync(long id) => (await Store.GetAsync(id, CancellationToken.None))!;

    private static string Reason(string reason) => new JsonObject { ["reason"] = reason }.ToJsonString();

    [Fact]
    public async Task Should_Discard_When_EditorWithReason()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var editor = _host.User(DashboardRoles.Editor, out var userId);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "discard", Reason("  sent twice by mistake  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.ToString());
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("discarded", body.GetProperty("status").GetString());
        Assert.Equal("sent twice by mistake", body.GetProperty("discardReason").GetString());
        Assert.Empty(body.GetProperty("allowedActions").EnumerateArray());
        Assert.Equal(userId, body.GetProperty("updated").GetProperty("by").GetProperty("id").GetString());
        var discarded = (await Store.EventsAsync(id, CancellationToken.None)).Last();
        Assert.Equal("discarded", discarded.Kind);
        Assert.Equal("sent twice by mistake", discarded.Detail!["reason"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Should_RequireReason_When_DiscardReasonBlank(string reason)
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "discard", Reason(reason));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SavedRequestStatus.Pending, (await StoredAsync(id)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"reason\":5}")]
    [InlineData("[]")]
    public async Task Should_RequireReason_When_DiscardBodyHasNoReason(string? body)
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "discard", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SavedRequestStatus.Pending, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task Should_RequireReason_When_DiscardReasonOver500Characters()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var editor = _host.User(DashboardRoles.Editor);

        using var tooLong = await SavedRequestTestHost.PostActionAsync(editor, id, "discard", Reason(new string('x', 501)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(SavedRequestStatus.Pending, (await StoredAsync(id)).Status);

        using var atLimit = await SavedRequestTestHost.PostActionAsync(editor, id, "discard", Reason(new string('x', 500)));
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
    }

    [Fact]
    public async Task Should_Return409_When_DiscardingNonPending()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        await Store.TryClaimAsync(id, null, "someone", false, DateTime.UtcNow, CancellationToken.None);
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "discard", Reason("no"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("posting", (await SavedRequestTestHost.BodyOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Should_Return412_When_DiscardIfMatchStale()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "discard", Reason("no"), "\"7\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(SavedRequestStatus.Pending, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task Should_Return403_When_EditorRestores()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var editor = _host.User(DashboardRoles.Editor);
        (await SavedRequestTestHost.PostActionAsync(editor, id, "discard", Reason("x"))).Dispose();

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "restore");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SavedRequestStatus.Discarded, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task Should_RestoreToPending_When_AdminRestores()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var editor = _host.User(DashboardRoles.Editor);
        (await SavedRequestTestHost.PostActionAsync(editor, id, "discard", Reason("x"))).Dispose();
        using var admin = _host.User(DashboardRoles.Admin, out var adminId);

        using var response = await SavedRequestTestHost.PostActionAsync(admin, id, "restore");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"3\"", response.Headers.ETag?.ToString());
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("discardReason").ValueKind);
        Assert.Contains("edit", body.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(adminId, body.GetProperty("updated").GetProperty("by").GetProperty("id").GetString());
        Assert.Equal("restored", (await Store.EventsAsync(id, CancellationToken.None)).Last().Kind);
        Assert.Null((await StoredAsync(id)).DiscardReason);
    }

    [Fact]
    public async Task Should_Return409_When_RestoringPending()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var admin = _host.User(DashboardRoles.Admin);

        using var response = await SavedRequestTestHost.PostActionAsync(admin, id, "restore");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("pending", (await SavedRequestTestHost.BodyOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Should_Return403_When_ViewerEditsOrDiscards()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var put = await SavedRequestTestHost.PutAsync(viewer, id, SavedRequestTestHost.Fixture("n8n-good.json"), "\"1\"");
        using var discard = await SavedRequestTestHost.PostActionAsync(viewer, id, "discard", Reason("x"));
        using var validate = await SavedRequestTestHost.PostActionAsync(viewer, id, "validate");

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, discard.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, validate.StatusCode);
        Assert.Equal(SavedRequestStatus.Pending, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task Should_Return404_When_DiscardingOrRestoringUnknownId()
    {
        using var admin = _host.User(DashboardRoles.Admin);

        using var discard = await SavedRequestTestHost.PostActionAsync(admin, 999, "discard", Reason("x"));
        using var restore = await SavedRequestTestHost.PostActionAsync(admin, 999, "restore");

        Assert.Equal(HttpStatusCode.NotFound, discard.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, restore.StatusCode);
    }
}
