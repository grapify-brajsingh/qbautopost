using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1015: <c>POST /api/v1/saved-requests/{id}/resolve</c> (saved-requests spec 10.6) and the ledger batch an admin
/// reads before choosing. An unknown post is settled by a person, with a note, and never by a retry.
/// </summary>
public sealed class ResolveApiTests : IDisposable
{
    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private string BatchesRoot => _host.Factory.Dir.Combine("data", "api-batches");

    /// <summary>Saves a fixture and posts it as an editor, with QuickBooks failing so the request ends <c>unknown</c>.</summary>
    private async Task<long> UnknownAsync()
    {
        var id = await SaveAsync(SyncPostFixtures.NoResponse);
        _host.Factory.Gateway.Throw = new QuickBooksCallException("ProcessRequest failed (0x80010105)", unchecked((int)0x80010105));
        using var editor = _host.User(DashboardRoles.Editor);
        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "post");
        _host.Factory.Gateway.Throw = null;
        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(id));
        return id;
    }

    private async Task<long> PartialAsync()
    {
        var id = await SaveAsync(SyncPostFixtures.Partial);
        _host.Factory.Gateway.RejectLine = 2;
        using var editor = _host.User(DashboardRoles.Editor);
        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "post");
        Assert.Equal(SavedRequestStatus.Partial, await StatusAsync(id));
        return id;
    }

    private async Task<long> SaveAsync(string fixture)
    {
        var (response, body) = await _host.SaveAsync(SyncPostFixtures.Read(fixture));
        using (response)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return body.GetProperty("id").GetInt64();
    }

    private async Task<SavedRequestStatus> StatusAsync(long id) => (await Store.GetAsync(id, CancellationToken.None))!.Status;

    private static string Body(string outcome, string? note) =>
        new JsonObject { ["outcome"] = outcome, ["note"] = note }.ToJsonString();

    private async Task<HttpResponseMessage> ResolveAsync(HttpClient client, long id, string body, string? ifMatch = null) =>
        await SavedRequestTestHost.PostActionAsync(client, id, "resolve", body, ifMatch);

    [Fact]
    public async Task Should_MoveUnknownToPostedAndCopyPostId_When_AdminResolvesPosted()
    {
        var id = await UnknownAsync();
        var postId = (await Store.AttemptsAsync(id, CancellationToken.None)).Single().PostId;
        Assert.NotNull(postId);
        using var admin = _host.User(DashboardRoles.Admin);

        using var response = await ResolveAsync(admin, id, Body("posted", "Checked QuickBooks, it is there."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("posted", body.GetProperty("status").GetString());
        Assert.Equal(postId, body.GetProperty("postId").GetString());
        Assert.DoesNotContain(body.GetProperty("allowedActions").EnumerateArray(), a => a.GetString() == "resolve");
        Assert.Equal(SavedRequestStatus.Posted, await StatusAsync(id));
    }

    [Fact]
    public async Task Should_MoveUnknownToPending_When_AdminResolvesNotPosted()
    {
        var id = await UnknownAsync();
        using var admin = _host.User(DashboardRoles.Admin);

        using var response = await ResolveAsync(admin, id, Body("not-posted", "Not in QuickBooks."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
    }

    [Fact]
    public async Task Should_ResolvePartialToPosted_When_AdminAcceptsItAsIs()
    {
        var id = await PartialAsync();
        using var admin = _host.User(DashboardRoles.Admin);

        using var response = await ResolveAsync(admin, id, Body("posted", "Accepted as is."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SavedRequestStatus.Posted, await StatusAsync(id));
    }

    [Fact]
    public async Task Should_Return409_When_ResolvingPartialToNotPosted()
    {
        var id = await PartialAsync();
        using var admin = _host.User(DashboardRoles.Admin);

        using var response = await ResolveAsync(admin, id, Body("not-posted", "Try again."));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("not-resolvable", problem.GetProperty("type").GetString());
        Assert.Contains("partial", problem.GetProperty("detail").GetString());
        Assert.Equal(SavedRequestStatus.Partial, await StatusAsync(id));
    }

    [Fact]
    public async Task Should_Return409_When_TheRequestIsNotUnknownOrPartial()
    {
        var id = await SaveAsync(SyncPostFixtures.Posted);
        using var admin = _host.User(DashboardRoles.Admin);

        using var response = await ResolveAsync(admin, id, Body("posted", "Why not."));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not-resolvable", (await SavedRequestTestHost.BodyOf(response)).GetProperty("type").GetString());
        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
    }

    [Theory]
    [InlineData("{ \"outcome\": \"posted\" }")]
    [InlineData("{ \"outcome\": \"posted\", \"note\": \"   \" }")]
    [InlineData("{ \"outcome\": \"posted\", \"note\": 5 }")]
    [InlineData("{ \"outcome\": \"maybe\", \"note\": \"x\" }")]
    [InlineData("{ \"note\": \"x\" }")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task Should_Return400_When_NoteOrOutcomeMissing(string body)
    {
        var id = await UnknownAsync();
        using var admin = _host.User(DashboardRoles.Admin);

        using var response = await ResolveAsync(admin, id, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(id));
    }

    [Fact]
    public async Task Should_Return400_When_NoteIsLongerThan500AfterTrim()
    {
        var id = await UnknownAsync();
        using var admin = _host.User(DashboardRoles.Admin);

        using var tooLong = await ResolveAsync(admin, id, Body("posted", new string('n', 501)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        using var exact = await ResolveAsync(admin, id, Body("posted", " " + new string('n', 500) + " "));
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
    }

    [Fact]
    public async Task Should_Return403_When_EditorResolves()
    {
        var id = await UnknownAsync();
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await ResolveAsync(editor, id, Body("posted", "Trust me."));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(id));
    }

    [Fact]
    public async Task Should_Return404_When_TheIdIsUnknown()
    {
        using var admin = _host.User(DashboardRoles.Admin);

        using var missing = await ResolveAsync(admin, 999, Body("posted", "x"));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Should_Return412_When_IfMatchIsStale()
    {
        var id = await UnknownAsync();
        using var admin = _host.User(DashboardRoles.Admin);

        using var response = await ResolveAsync(admin, id, Body("posted", "x"), ifMatch: "\"99\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(id));
    }

    [Fact]
    public async Task Should_StoreResolverAndNoteOnAttemptAndEvent()
    {
        var id = await UnknownAsync();
        using var admin = _host.User(DashboardRoles.Admin, out var adminId);

        using var response = await ResolveAsync(admin, id, Body("posted", "  Checked QuickBooks.  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempt = Assert.Single(await Store.AttemptsAsync(id, CancellationToken.None));
        Assert.Equal("Checked QuickBooks.", attempt.ResolutionNote);
        Assert.Equal(adminId, attempt.ResolvedBy);
        Assert.NotNull(attempt.ResolvedUtc);
        var resolved = Assert.Single(await Store.EventsAsync(id, CancellationToken.None), e => e.Kind == SavedRequestEvents.Resolved);
        Assert.Equal(adminId, resolved.Actor);
        Assert.Equal("Checked QuickBooks.", resolved.Detail!["note"]!.GetValue<string>());

        using var viewer = _host.User(DashboardRoles.Viewer);
        using var read = await viewer.GetAsync($"{SavedRequestTestHost.Route}/{id}/attempts/{attempt.Id}");
        var shown = await SavedRequestTestHost.BodyOf(read);
        Assert.Equal("Checked QuickBooks.", shown.GetProperty("resolutionNote").GetString());
        Assert.Equal(adminId, shown.GetProperty("resolvedBy").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Should_ShowLedgerBatch_When_AttemptHasPostId()
    {
        var id = await PartialAsync();
        var attempt = Assert.Single(await Store.AttemptsAsync(id, CancellationToken.None));
        using var admin = _host.User(DashboardRoles.Admin);

        using var response = await admin.GetAsync($"{SavedRequestTestHost.Route}/{id}/attempts/{attempt.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = (await SavedRequestTestHost.BodyOf(response)).GetProperty("ledgerBatch");
        Assert.Equal(attempt.PostId, batch.GetProperty("batchId").GetString());
        Assert.Equal("partial", batch.GetProperty("status").GetString());
        Assert.True(batch.GetProperty("counts").GetProperty("posted").GetInt32() >= 1);
        var posted = batch.GetProperty("posted");
        Assert.NotEmpty(posted.EnumerateArray());
        Assert.All(posted.EnumerateArray(), row => Assert.False(string.IsNullOrEmpty(row.GetProperty("txnId").GetString())));
    }

    [Fact]
    public async Task Should_OmitLedgerBatchAndStillAnswer_When_TheBatchFileIsMissingOrUnreadable()
    {
        var id = await PartialAsync();
        var attempt = Assert.Single(await Store.AttemptsAsync(id, CancellationToken.None));
        var folder = Path.Combine(BatchesRoot, attempt.PostId!);
        using var admin = _host.User(DashboardRoles.Admin);

        File.WriteAllText(Path.Combine(folder, "result.json"), "{ not json");
        File.WriteAllText(Path.Combine(folder, "status.json"), "{ not json");
        using var unreadable = await admin.GetAsync($"{SavedRequestTestHost.Route}/{id}/attempts/{attempt.Id}");
        Directory.Delete(folder, recursive: true);
        using var missing = await admin.GetAsync($"{SavedRequestTestHost.Route}/{id}/attempts/{attempt.Id}");

        foreach (var response in new[] { unreadable, missing })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await SavedRequestTestHost.BodyOf(response);
            Assert.False(body.TryGetProperty("ledgerBatch", out _));
            Assert.Equal(attempt.PostId, body.GetProperty("postId").GetString());
        }
    }

    [Fact]
    public async Task Should_OmitLedgerBatch_When_AttemptHasNoPostId()
    {
        var id = await SaveAsync(SyncPostFixtures.Held);
        using var editor = _host.User(DashboardRoles.Editor);
        using (await SavedRequestTestHost.PostActionAsync(editor, id, "post"))
        {
        }

        var attempt = Assert.Single(await Store.AttemptsAsync(id, CancellationToken.None));
        using var response = await editor.GetAsync($"{SavedRequestTestHost.Route}/{id}/attempts/{attempt.Id}");

        Assert.False((await SavedRequestTestHost.BodyOf(response)).TryGetProperty("ledgerBatch", out _));
    }
}
