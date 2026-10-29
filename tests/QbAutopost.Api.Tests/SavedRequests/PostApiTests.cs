using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QbAutopost.Api.Database;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Api;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;
using QbAutopost.Core.Store;
using QbAutopost.Data;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1014: <c>POST /api/v1/saved-requests/{id}/post</c> (saved-requests spec 7.2, 9, 10.2-10.6). The claim is
/// committed before the engine runs, the engine is validate-and-post's, every ending is recorded on the attempt, and
/// nothing that may have reached QuickBooks ever returns the request to <c>pending</c>.
/// </summary>
public sealed class PostApiTests : IDisposable
{
    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private FakeQbGateway Gateway => _host.Factory.Gateway;

    private string BatchesRoot => _host.Factory.Dir.Combine("data", "api-batches");

    private async Task<long> SaveAsync(string fixture)
    {
        var (response, body) = await _host.SaveAsync(SyncPostFixtures.Read(fixture));
        using (response)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return body.GetProperty("id").GetInt64();
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(
        HttpClient client, long id, string? body = null, string? ifMatch = null)
    {
        var response = await SavedRequestTestHost.PostActionAsync(client, id, "post", body, ifMatch);
        return (response, await SavedRequestTestHost.BodyOf(response));
    }

    private async Task<JsonElement> GetAsync(long id)
    {
        using var reader = _host.User(DashboardRoles.Viewer);
        using var response = await reader.GetAsync($"{SavedRequestTestHost.Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    private async Task<JsonElement> AttemptAsync(long id, long attemptId)
    {
        using var reader = _host.User(DashboardRoles.Viewer);
        using var response = await reader.GetAsync($"{SavedRequestTestHost.Route}/{id}/attempts/{attemptId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    private async Task<IReadOnlyList<PostAttempt>> AttemptsAsync(long id) => await Store.AttemptsAsync(id, CancellationToken.None);

    private async Task<SavedRequestStatus> StatusAsync(long id) => (await Store.GetAsync(id, CancellationToken.None))!.Status;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(10);
        }
    }

    // SR-5, SR-7
    [Fact]
    public async Task Should_RecordPostedAttemptAndLock_When_Posted()
    {
        var id = await SaveAsync(SyncPostFixtures.Posted);
        using var editor = _host.User(DashboardRoles.Editor, out var editorId);

        var (response, body) = await PostAsync(editor, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("posted", body.GetProperty("status").GetString());
        Assert.Equal("posted", body.GetProperty("savedRequestStatus").GetString());
        Assert.Equal(id, body.GetProperty("savedRequestId").GetInt64());
        var postId = body.GetProperty("postId").GetString();
        var attempt = await AttemptAsync(id, body.GetProperty("attemptId").GetInt64());
        Assert.Equal("posted", attempt.GetProperty("outcome").GetString());
        Assert.Equal(200, attempt.GetProperty("httpStatus").GetInt32());
        Assert.Equal(postId, attempt.GetProperty("postId").GetString());
        Assert.Equal(1, attempt.GetProperty("versionNumber").GetInt32());
        Assert.Equal(editorId, attempt.GetProperty("startedBy").GetProperty("id").GetString());
        Assert.Equal(2, attempt.GetProperty("counts").GetProperty("posted").GetInt32());
        Assert.Equal(postId, attempt.GetProperty("result").GetProperty("postId").GetString());
        Assert.Equal(body.GetProperty("posted").GetRawText(), attempt.GetProperty("result").GetProperty("posted").GetRawText());
        var record = await GetAsync(id);
        Assert.Equal("posted", record.GetProperty("status").GetString());
        Assert.Equal(postId, record.GetProperty("postId").GetString());

        var etag = $"\"{record.GetProperty("rowVersion").GetInt32()}\"";
        using var edit = await SavedRequestTestHost.PutAsync(editor, id, SyncPostFixtures.Read(SyncPostFixtures.Posted), etag);
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        var (again, problem) = await PostAsync(editor, id);
        using (again)
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("already-posted", problem.GetProperty("type").GetString());
            Assert.Equal(postId, problem.GetProperty("postId").GetString());
        }

        Assert.Single(Gateway.Writes);
        Assert.Single(await AttemptsAsync(id));
        response.Dispose();
    }

    [Fact]
    public async Task Should_ReturnToPendingWithHeldAttempt_When_422()
    {
        var id = await SaveAsync(SyncPostFixtures.Held);
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(422, problem.GetProperty("status").GetInt32());
            Assert.Equal("pending", problem.GetProperty("savedRequestStatus").GetString());
            Assert.Equal("row-2", problem.GetProperty("validation").EnumerateArray().Single().GetProperty("externalId").GetString());
        }

        Assert.Empty(Gateway.Requests);
        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Held, attempt.Outcome);
        Assert.Equal(422, attempt.HttpStatus);
        Assert.Equal(1, attempt.Counts!["held"]!.GetValue<int>());
        Assert.Equal(422, attempt.Result!["status"]!.GetValue<int>());
    }

    // Spec 10.4
    [Fact]
    public async Task Should_PassRetryAfter_When_Busy()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        var queue = _host.Factory.Services.GetRequiredService<JobQueue>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = queue.RunExclusiveAsync("folder-job", async _ => { await release.Task; return 0; }, CancellationToken.None);
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
            Assert.Equal(5, problem.GetProperty("retryAfterSeconds").GetInt32());
            Assert.Equal("pending", problem.GetProperty("savedRequestStatus").GetString());
        }

        release.SetResult();
        await busy;
        Assert.Empty(Gateway.Requests);
        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Busy, attempt.Outcome);
        Assert.Equal(409, attempt.HttpStatus);
    }

    /// <summary>Spec 7.2's builder's call: the qbXML was sent and nobody knows if it landed, so it is never one click from a re-post.</summary>
    [Fact]
    public async Task Should_MarkUnknown_When_QuickBooksNoResponse()
    {
        var id = await SaveAsync(SyncPostFixtures.NoResponse);
        Gateway.Throw = new QuickBooksCallException("ProcessRequest failed (0x80010105)", unchecked((int)0x80010105));
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("failed", body.GetProperty("status").GetString());
            Assert.Equal("unknown", body.GetProperty("savedRequestStatus").GetString());
        }

        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(id));
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Unknown, attempt.Outcome);
        Assert.Equal(200, attempt.HttpStatus);
        Assert.Equal(body.GetProperty("postId").GetString(), attempt.PostId);

        Gateway.Throw = null;
        var (again, problem) = await PostAsync(editor, id);
        using (again)
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("not-postable", problem.GetProperty("type").GetString());
        }

        Assert.Single(Gateway.Writes);
    }

    /// <summary>
    /// SPEC-GAP T-1014: a write that outlives the busy timeout was sent, and QuickBooks may still apply it
    /// (<c>quickbooks-busy</c>, "may or may not have been applied"). It must not return to pending either.
    /// </summary>
    [Fact]
    public async Task Should_MarkUnknown_When_TheWriteOutlivesTheBusyTimeout()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        Gateway.Throw = new QuickBooksBusyException("simulated: no answer within the busy timeout");
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("failed", body.GetProperty("status").GetString());
            Assert.Equal("unknown", body.GetProperty("savedRequestStatus").GetString());
        }

        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(id));
        Assert.Equal(AttemptOutcome.Unknown, Assert.Single(await AttemptsAsync(id)).Outcome);
    }

    /// <summary>
    /// R31: QuickBooks created the transaction but echoed another amount, so G5 holds it as <c>amount-mismatch</c> and
    /// the post is <c>failed</c>. The transaction exists, and G4 matches on amount, so a re-post would double-post: the
    /// request locks as unknown and the second post is refused without a write.
    /// </summary>
    [Fact]
    public async Task Should_MarkUnknown_When_QuickBooksEchoesAnotherAmount()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        Gateway.MisstateAmountLine = 1;
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("failed", body.GetProperty("status").GetString());
            Assert.Equal("amount-mismatch", body.GetProperty("held")[0].GetProperty("reason").GetString());
            Assert.Equal("unknown", body.GetProperty("savedRequestStatus").GetString());
        }

        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(id));
        Assert.Equal(AttemptOutcome.Unknown, Assert.Single(await AttemptsAsync(id)).Outcome);
        Assert.Single(Gateway.Company.Transactions);

        Gateway.MisstateAmountLine = null;
        var (again, problem) = await PostAsync(editor, id);
        using (again)
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("not-postable", problem.GetProperty("type").GetString());
        }

        Assert.Single(Gateway.Writes);
    }

    /// <summary>R31: QuickBooks refused the add with a non-zero status, so nothing was created and the request is pending again.</summary>
    [Fact]
    public async Task Should_ReturnToPending_When_QuickBooksRejectsEveryRow()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        Gateway.RejectLine = 1;
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("failed", body.GetProperty("status").GetString());
            Assert.Equal("pending", body.GetProperty("savedRequestStatus").GetString());
        }

        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
        Assert.Equal(AttemptOutcome.Failed, Assert.Single(await AttemptsAsync(id)).Outcome);
        Assert.Empty(Gateway.Company.Transactions);
    }

    /// <summary>R3: an exception once the worker has started the post may follow a send, so it is unknown, never pending.</summary>
    [Fact]
    public async Task Should_MarkUnknown_When_TheEngineThrowsAfterTheWorkStarted()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        Gateway.Throw = new OperationCanceledException("simulated: not ours");
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("post-outcome-unknown", problem.GetProperty("type").GetString());
            Assert.Equal("unknown", problem.GetProperty("savedRequestStatus").GetString());
            Assert.DoesNotContain("simulated", problem.GetRawText());
        }

        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(id));
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Unknown, attempt.Outcome);
        Assert.Null(attempt.HttpStatus);
        Assert.Equal("post-outcome-unknown: OperationCanceledException", attempt.Error);
        Assert.DoesNotContain("simulated", attempt.Result!.ToJsonString());
    }

    /// <summary>R3: an exception before the worker took the post sent nothing, so the request may be posted again.</summary>
    [Fact]
    public async Task Should_ReturnToPendingWithErrorAttempt_When_TheEngineThrowsBeforeTheWorkStarted()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        const string secret = @"C:\secret\rules.json";
        using var host = _host.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<DirectPostRunner>();
            services.AddSingleton<DirectPostRunner>(sp =>
                ActivatorUtilities.CreateInstance<ThrowingRunner>(sp, new InvalidOperationException("bad state at " + secret)));
        }));
        using var editor = host.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.Factory.Users.TokenFor(DashboardRoles.Editor));

        var (response, problem) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("post-not-started", problem.GetProperty("type").GetString());
            Assert.Equal("pending", problem.GetProperty("savedRequestStatus").GetString());
            Assert.DoesNotContain("secret", problem.GetRawText());
        }

        Assert.Empty(Gateway.Requests);
        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Error, attempt.Outcome);
        Assert.Equal("post-not-started: InvalidOperationException", attempt.Error);
        Assert.DoesNotContain("bad state", attempt.Result!.ToJsonString());
        Assert.DoesNotContain("secret", attempt.Result!.ToJsonString());
        Assert.Equal(500, attempt.HttpStatus);
    }

    /// <summary>Spec 7.2 and 10.8: a missing rules file is <c>unavailable</c>, answered 503 as T-920 does.</summary>
    [Fact]
    public async Task Should_ReturnToPendingWith503_When_TheRulesFileIsMissing()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        using var host = _host.Factory.WithSetting("Company:RulesFile", _host.Factory.Dir.Combine("no-such-rules.json"));
        using var editor = host.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.Factory.Users.TokenFor(DashboardRoles.Editor));

        var (response, problem) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("The rules file is missing", problem.GetProperty("title").GetString());
            Assert.Equal("pending", problem.GetProperty("savedRequestStatus").GetString());
        }

        Assert.Empty(Gateway.Requests);
        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Unavailable, attempt.Outcome);
        Assert.Equal(503, attempt.HttpStatus);
    }

    [Fact]
    public async Task Should_MarkPartial_When_OneRowRejectedByG5()
    {
        var id = await SaveAsync(SyncPostFixtures.Partial);
        Gateway.RejectLine = 2;
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("partial", body.GetProperty("status").GetString());
            Assert.Equal("partial", body.GetProperty("savedRequestStatus").GetString());
        }

        Assert.Equal(SavedRequestStatus.Partial, await StatusAsync(id));
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Partial, attempt.Outcome);
        Assert.Equal(1, attempt.Counts!["posted"]!.GetValue<int>());
        Assert.Equal(1, attempt.Counts!["held"]!.GetValue<int>());
    }

    // SR-6, spec 10.2
    [Fact]
    public async Task Should_PostOnlyOnce_When_TwoConcurrentPosts()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gateway.WriteGate = release.Task;
        using var one = _host.User(DashboardRoles.Editor);
        using var two = _host.User(DashboardRoles.Editor);

        var first = PostAsync(one, id);
        var second = PostAsync(two, id);
        var loserTask = await Task.WhenAny(first, second);
        var (loser, problem) = await loserTask;
        release.SetResult();
        var (winner, body) = await (loserTask == first ? second : first);

        using (loser)
        using (winner)
        {
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            Assert.Equal("already-posting", problem.GetProperty("type").GetString());
            Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
            Assert.Equal(body.GetProperty("attemptId").GetInt64(), problem.GetProperty("attemptId").GetInt64());
        }

        Assert.Single(await AttemptsAsync(id));
        Assert.Single(Gateway.Writes);
        Assert.Equal(SavedRequestStatus.Posted, await StatusAsync(id));
    }

    // Spec 9
    [Fact]
    public async Task Should_Return409DuplicateUnconfirmed_When_FlaggedAndNoConfirm()
    {
        var original = await SaveAsync(SyncPostFixtures.PostedOneRow);
        var copy = await SaveAsync(SyncPostFixtures.PostedOneRow);
        using var editor = _host.User(DashboardRoles.Editor, out var editorId);

        var (refused, problem) = await PostAsync(editor, copy);

        using (refused)
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("duplicate-unconfirmed", problem.GetProperty("type").GetString());
            Assert.Equal(original, problem.GetProperty("duplicate").GetProperty("of").GetInt64());
            Assert.Equal("pending", problem.GetProperty("duplicate").GetProperty("status").GetString());
        }

        Assert.Empty(await AttemptsAsync(copy));
        Assert.Empty(Gateway.Requests);

        var (confirmed, body) = await PostAsync(editor, copy, """{ "confirmDuplicate": true }""");

        using (confirmed)
        {
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            Assert.Equal("posted", body.GetProperty("savedRequestStatus").GetString());
        }

        var record = await GetAsync(copy);
        Assert.Equal(editorId, record.GetProperty("duplicate").GetProperty("acknowledgedBy").GetProperty("id").GetString());
        Assert.NotEqual(JsonValueKind.Null, record.GetProperty("duplicate").GetProperty("acknowledgedUtc").ValueKind);
    }

    [Fact]
    public async Task Should_Return400_When_TheBodyIsNotAnObjectWithABooleanConfirmDuplicate()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostAsync(editor, id, """{ "confirmDuplicate": "yes" }""");

        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid-request", problem.GetProperty("type").GetString());
        }

        Assert.Empty(await AttemptsAsync(id));
    }

    // Spec 10.2
    [Fact]
    public async Task Should_Return412_When_IfMatchStale()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostAsync(editor, id, ifMatch: "\"99\"");

        using (response)
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            Assert.Equal("edited-by-someone-else", problem.GetProperty("type").GetString());
        }

        Assert.Empty(await AttemptsAsync(id));
        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
        Assert.Empty(Gateway.Requests);
    }

    [Fact]
    public async Task Should_Post_When_IfMatchIsCurrent()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        var etag = $"\"{(await GetAsync(id)).GetProperty("rowVersion").GetInt32()}\"";
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, _) = await PostAsync(editor, id, ifMatch: etag);

        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Should_Return404_When_TheIdIsUnknown()
    {
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostAsync(editor, 999);

        using (response)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not-found", problem.GetProperty("type").GetString());
        }
    }

    // Spec 7.2: the route binds the stored payload itself, so the 400 names each path (Q-79).
    [Fact]
    public async Task Should_Return400WithPaths_When_CurrentVersionUnreadable()
    {
        var (saved, savedBody) = await _host.SaveAsync(SavedRequestTestHost.Fixture("unreadable-date.json"));
        saved.Dispose();
        var id = savedBody.GetProperty("id").GetInt64();
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetString();
            Assert.StartsWith("$.transactions[0].date", error);
            Assert.Equal("pending", problem.GetProperty("savedRequestStatus").GetString());
        }

        Assert.Empty(Gateway.Requests);
        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Refused, attempt.Outcome);
        Assert.Equal(400, attempt.HttpStatus);
    }

    // Spec 10.5
    [Fact]
    public async Task Should_FinishAndRecord_When_ClientDisconnects()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gateway.WriteGate = release.Task;
        var probe = new AbortProbe();
        using var host = _host.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(probe)));
        using var editor = host.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.Factory.Users.TokenFor(DashboardRoles.Editor));
        using var cancel = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SavedRequestTestHost.Route}/{id}/post");

        var call = editor.SendAsync(request, cancel.Token);
        await WaitUntilAsync(() => Gateway.Writes.Count == 1);
        cancel.Cancel();
        // The server has seen the disconnect while the qbXML is still with QuickBooks.
        await probe.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("posting", (await GetAsync(id)).GetProperty("status").GetString());
        release.SetResult();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonElement record;
        while ((record = await GetAsync(id)).GetProperty("status").GetString() == "posting")
        {
            Assert.True(DateTime.UtcNow < deadline, "the post never finished");
            await Task.Delay(10);
        }

        Assert.Equal("posted", record.GetProperty("status").GetString());
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Posted, attempt.Outcome);
        Assert.Equal(200, attempt.HttpStatus);
        Assert.NotNull(attempt.Result);
        Assert.Single(Gateway.Writes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    // Spec 10.6, review focus 4
    [Fact]
    public async Task Should_WriteEvidenceFileAndStayPosting_When_StoreFailsOnComplete()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        Store.FailCompletes = 3;
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostAsync(editor, id);

        string postId;
        long attemptId;
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("posting", body.GetProperty("savedRequestStatus").GetString());
            postId = body.GetProperty("postId").GetString()!;
            attemptId = body.GetProperty("attemptId").GetInt64();
        }

        Assert.Equal(3, Store.CompleteCalls);
        var file = Path.Combine(new ApiBatchStore(BatchesRoot).FolderOf(postId), "saved-request.json");
        Assert.True(File.Exists(file), file);
        var text = await File.ReadAllTextAsync(file);
        using var evidence = JsonDocument.Parse(text);
        Assert.Equal(id, evidence.RootElement.GetProperty("savedRequestId").GetInt64());
        Assert.Equal(attemptId, evidence.RootElement.GetProperty("attemptId").GetInt64());
        Assert.Equal("posted", evidence.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(200, evidence.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Equal(postId, evidence.RootElement.GetProperty("result").GetProperty("postId").GetString());
        Assert.DoesNotContain(ApiFactory.ApiKey, text);
        Assert.DoesNotContain(ApiFactory.SigningKey, text);
        Assert.DoesNotContain(_host.N8nKey, text);
        Assert.Equal(SavedRequestStatus.Posting, await StatusAsync(id));

        // A restart: a new host over the same database; its start-up recovery runs before any route is served (spec 10.6).
        using var restarted = ApiFactory.Restarted(Store);
        using var reader = restarted.CreateClient();
        reader.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", restarted.Users.TokenFor(DashboardRoles.Viewer));
        using var after = await reader.GetAsync($"{SavedRequestTestHost.Route}/{id}");

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal("unknown", (await SavedRequestTestHost.BodyOf(after)).GetProperty("status").GetString());
        Assert.Equal(AttemptOutcome.Unknown, Assert.Single(await AttemptsAsync(id)).Outcome);
        Assert.Single(Gateway.Writes);
        Assert.Empty(restarted.Gateway.Requests);
    }

    /// <summary>
    /// M10 review: the first try's commit landed but its acknowledgement was lost. The retry finds the attempt already
    /// completed with the same outcome, which is success: the real status is answered and no evidence file is written.
    /// </summary>
    [Fact]
    public async Task Should_AnswerTheRecordedStatus_When_TheFirstRecordingLandedButItsAcknowledgementWasLost()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        Store.LoseCompleteAcknowledgements = 1;
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostAsync(editor, id);

        string postId;
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("posted", body.GetProperty("savedRequestStatus").GetString());
            postId = body.GetProperty("postId").GetString()!;
        }

        Assert.Equal(1, Store.CompleteCalls);
        Assert.Equal(SavedRequestStatus.Posted, await StatusAsync(id));
        Assert.Equal(AttemptOutcome.Posted, Assert.Single(await AttemptsAsync(id)).Outcome);
        Assert.False(File.Exists(Path.Combine(new ApiBatchStore(BatchesRoot).FolderOf(postId), "saved-request.json")));
        Assert.False(Directory.Exists(Path.Combine(BatchesRoot, "saved-requests")));
    }

    /// <summary>
    /// Fix round 1: anything that stops the result being turned into an answer (here the host's serialiser refuses a
    /// <c>SyncPostResult</c>) must not lose what QuickBooks said: the attempt is <c>unknown</c>, never pending, and the
    /// raw result is in the evidence file.
    /// </summary>
    [Fact]
    public async Task Should_RecordUnknownAndWriteEvidence_When_TheResultCannotBeSerialised()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        using var host = _host.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
                o.SerializerOptions.Converters.Add(new RefusingConverter()))));
        using var editor = host.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.Factory.Users.TokenFor(DashboardRoles.Editor));

        var (response, problem) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("post-outcome-unknown", problem.GetProperty("type").GetString());
            Assert.Equal("unknown", problem.GetProperty("savedRequestStatus").GetString());
            Assert.DoesNotContain(RefusingConverter.Message, problem.GetRawText());
        }

        Assert.Single(Gateway.Writes);
        Assert.Equal(SavedRequestStatus.Unknown, await StatusAsync(id));
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Unknown, attempt.Outcome);
        Assert.DoesNotContain(RefusingConverter.Message, attempt.Error);
        var batch = Assert.Single(Directory.GetDirectories(BatchesRoot));
        var file = Path.Combine(batch, "saved-request.json");
        Assert.True(File.Exists(file), file);
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal(id, evidence.RootElement.GetProperty("savedRequestId").GetInt64());
        Assert.Equal("unknown", evidence.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(attempt.PostId, evidence.RootElement.GetProperty("rawResult").GetProperty("postId").GetString());
        Assert.Equal("FAKE-1", evidence.RootElement.GetProperty("rawResult").GetProperty("posted")[0].GetProperty("txnId").GetString());
    }

    /// <summary>
    /// Fix round 1, spec 9: an edit committed between the duplicate check and the claim (here it flags the request)
    /// makes the claim stale. The person is told to reload; nothing they did not see is posted.
    /// </summary>
    [Fact]
    public async Task Should_Return412AndPostNothing_When_TheRequestChangesBetweenTheCheckAndTheClaim()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        Store.BeforeNextClaim = () => Store.Change(id, r => r with { DuplicateOfId = 99, DuplicateReasons = ["same-payload"] });
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostAsync(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            Assert.Equal("edited-by-someone-else", problem.GetProperty("type").GetString());
        }

        Assert.Empty(await AttemptsAsync(id));
        Assert.Empty(Gateway.Requests);
        Assert.Equal(SavedRequestStatus.Pending, await StatusAsync(id));
    }

    [Fact]
    public async Task Should_Return403_When_ViewerOrApiKeyPosts()
    {
        var id = await SaveAsync(SyncPostFixtures.PostedOneRow);
        using var viewer = _host.User(DashboardRoles.Viewer);
        using var legacy = _host.Factory.CreateAuthorizedClient();
        using var n8n = _host.N8n();

        foreach (var client in new[] { viewer, legacy, n8n })
        {
            var (response, _) = await PostAsync(client, id);
            using (response)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }

        Assert.Empty(await AttemptsAsync(id));
        Assert.Empty(Gateway.Requests);
    }

    /// <summary>Completes when the server sees a post request aborted by its client.</summary>
    private sealed class AbortProbe : IStartupFilter
    {
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, rest) =>
            {
                if (context.Request.Path.Value?.EndsWith("/post", StringComparison.Ordinal) == true)
                {
                    context.RequestAborted.Register(() => Aborted.TrySetResult());
                }

                await rest(context);
            });
            next(app);
        };
    }

    /// <summary>Refuses to write a <see cref="SyncPostResult"/>, as a serialiser fault would.</summary>
    private sealed class RefusingConverter : System.Text.Json.Serialization.JsonConverter<SyncPostResult>
    {
        public const string Message = "simulated serialiser fault";

        public override SyncPostResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, SyncPostResult value, JsonSerializerOptions options) =>
            throw new InvalidOperationException(Message);
    }

    private sealed class ThrowingRunner(
        Exception failure, PipelineOptions options, DirectPlanner planner, ApiBatchStore batches, IQbGateway gateway,
        IClock clock, ILogger<DirectPostRunner> log, QbAutopost.Core.Abstractions.IQbListsSource lists)
        : DirectPostRunner(options, planner, batches, gateway, clock, log, lists)
    {
        public override DirectPlan Plan(DirectRequest request) => throw failure;
    }
}
