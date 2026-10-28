using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1113 (dashboard v2 spec 6.2, 6.3, 10 items 4.4 and 4.11, 13.2): <c>POST /api/v2/saved-requests/{id}/post</c>. A row
/// marked <c>post: false</c> is listed under <c>skipped</c> as <c>not-to-post</c> and never reaches QuickBooks; every
/// row off is <c>400 nothing-to-post</c> with no attempt; a <c>post</c> that is not a boolean is a <c>refused</c>
/// attempt; the two 409s name the attempt and who started it; and a long post is not cut by the server.
/// </summary>
public sealed class V2PostTests : IDisposable
{
    private const string V2Route = "/api/v2/saved-requests";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private FakeQbGateway Gateway => _host.Factory.Gateway;

    private static string V2Fixture(string name) => File.ReadAllText(Fixtures.PathOf("saved-requests-v2", name));

    private static string WithChange(string json, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(json)!;
        change(node);
        return node.ToJsonString();
    }

    private async Task<long> SaveV2Async(string body)
    {
        using var client = _host.N8n();
        using var response = await client.PostAsync(V2Route, SavedRequestTestHost.Json(body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SavedRequestTestHost.BodyOf(response)).GetProperty("id").GetInt64();
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostV2Async(HttpClient client, long id)
    {
        var response = await client.PostAsync($"{V2Route}/{id}/post", null);
        return (response, await SavedRequestTestHost.BodyOf(response));
    }

    private async Task<JsonElement> DetailV2Async(long id)
    {
        using var reader = _host.User(DashboardRoles.Viewer);
        using var response = await reader.GetAsync($"{V2Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    private async Task<IReadOnlyList<PostAttempt>> AttemptsAsync(long id) => await Store.AttemptsAsync(id, CancellationToken.None);

    private async Task<SavedRequest> StoredAsync(long id) => (await Store.GetAsync(id, CancellationToken.None))!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(10);
        }
    }

    // ---- post: false (spec 6.2, 6.3) --------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ListNotPostedRowUnderSkipped_When_PostedById()
    {
        var id = await SaveV2Async(V2Fixture("post-false.json"));
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostV2Async(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("posted", body.GetProperty("status").GetString());
            Assert.Equal("posted", body.GetProperty("savedRequestStatus").GetString());
            var skipped = Assert.Single(body.GetProperty("skipped").EnumerateArray());
            Assert.Equal("not-to-post", skipped.GetProperty("reason").GetString());
            Assert.Equal(1, skipped.GetProperty("index").GetInt32());
            Assert.Equal("row-2", skipped.GetProperty("externalId").GetString());
            Assert.Equal(1, body.GetProperty("counts").GetProperty("skipped").GetInt32());
            Assert.Equal(0, body.GetProperty("counts").GetProperty("held").GetInt32());
            Assert.Empty(body.GetProperty("held").EnumerateArray());
        }
    }

    [Fact]
    public async Task Should_NotSendNotPostedRowToGateway_When_PostedById()
    {
        var id = await SaveV2Async(V2Fixture("post-false.json"));
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, _) = await PostV2Async(editor, id);
        response.Dispose();

        var write = Assert.Single(Gateway.Writes);
        Assert.Contains("HOME DEPOT #6412", write);
        Assert.Contains("184.32", write);
        Assert.All(Gateway.Requests, qbxml =>
        {
            Assert.DoesNotContain("SHELL OIL 4491", qbxml);
            Assert.DoesNotContain("99.10", qbxml);
        });
    }

    [Fact]
    public async Task Should_PostOnlyOnRows_When_PostFalseFixture()
    {
        var id = await SaveV2Async(V2Fixture("held-row-off.json"));
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostV2Async(editor, id);

        using (response)
        {
            // The off row would be held for its unknown payee if it were posted; it is skipped instead.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, body.GetProperty("counts").GetProperty("posted").GetInt32());
            Assert.Equal(
                new[] { 0, 2 },
                body.GetProperty("posted").EnumerateArray().Select(r => r.GetProperty("index").GetInt32()).ToArray());
        }

        var write = Assert.Single(Gateway.Writes);
        Assert.DoesNotContain("NEVER HEARD OF THEM", write);
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Posted, attempt.Outcome);
        Assert.Equal(SavedRequestStatus.Posted, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task Should_KeepControlTotal_When_RowsSkipped()
    {
        var id = await SaveV2Async(V2Fixture("post-false.json"));
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, _) = await PostV2Async(editor, id);
        response.Dispose();

        var stored = await StoredAsync(id);
        Assert.Equal(SavedRequestStatus.Posted, stored.Status);
        Assert.Equal(283.42m, stored.ControlTotal);
        Assert.Equal(283.42m, stored.CurrentPayload.GetProperty("controlTotal").GetDecimal());
        Assert.Equal(1, stored.CurrentVersion);
    }

    [Fact]
    public async Task Should_Answer400NothingToPost_When_EveryRowIsPostFalse()
    {
        var body = WithChange(V2Fixture("post-false.json"), n => n["transactions"]![0]!["post"] = false);
        var id = await SaveV2Async(body);
        var before = await StoredAsync(id);
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostV2Async(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nothing-to-post", problem.GetProperty("type").GetString());
            Assert.StartsWith("nothing-to-post:", problem.GetProperty("detail").GetString());
        }

        Assert.Empty(await AttemptsAsync(id));
        Assert.Empty(Gateway.Requests);
        var after = await StoredAsync(id);
        Assert.Equal(SavedRequestStatus.Pending, after.Status);
        Assert.Equal(before.RowVersion, after.RowVersion);
    }

    [Fact]
    public async Task Should_RecordRefusedAttempt_When_PostValueInvalid()
    {
        var body = WithChange(V2Fixture("post-false.json"), n => n["transactions"]![1]!["post"] = "no");
        var id = await SaveV2Async(body);
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostV2Async(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetString();
            Assert.Equal("$.transactions[1].post: must be true or false", error);
            Assert.Equal("pending", problem.GetProperty("savedRequestStatus").GetString());
        }

        Assert.Empty(Gateway.Requests);
        Assert.Equal(SavedRequestStatus.Pending, (await StoredAsync(id)).Status);
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Refused, attempt.Outcome);
        Assert.Equal(400, attempt.HttpStatus);
        var events = await Store.EventsAsync(id, CancellationToken.None);
        Assert.Contains(events, e => e.Kind == "post-finished");
    }

    // ---- 409 extras (spec 10 item 4.4) ----------------------------------------------------------------------------

    [Fact]
    public async Task Should_NameWhoStarted_When_AlreadyPosting()
    {
        var id = await SaveV2Async(SyncPostFixtures.Read(SyncPostFixtures.PostedOneRow));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gateway.WriteGate = release.Task;
        using var first = _host.User(DashboardRoles.Editor, out var firstId);
        using var second = _host.User(DashboardRoles.Editor);

        var running = PostV2Async(first, id);
        await WaitUntilAsync(() => Gateway.Writes.Count == 1);
        var (loser, problem) = await PostV2Async(second, id);
        release.SetResult();
        var (winner, body) = await running;

        using (loser)
        using (winner)
        {
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            Assert.Equal("already-posting", problem.GetProperty("type").GetString());
            Assert.Equal(body.GetProperty("attemptId").GetInt64(), problem.GetProperty("attemptId").GetInt64());
            Assert.Equal(firstId, problem.GetProperty("startedBy").GetProperty("id").GetString());
            Assert.False(string.IsNullOrEmpty(problem.GetProperty("startedBy").GetProperty("name").GetString()));
            Assert.NotEqual(firstId, problem.GetProperty("startedBy").GetProperty("name").GetString());
            var attempt = Assert.Single(await AttemptsAsync(id));
            Assert.Equal(attempt.StartedUtc, problem.GetProperty("startedUtc").GetDateTime().ToUniversalTime());
        }

        Assert.Single(Gateway.Writes);
    }

    [Fact]
    public async Task Should_NameAttempt_When_AlreadyPosted()
    {
        var id = await SaveV2Async(SyncPostFixtures.Read(SyncPostFixtures.PostedOneRow));
        using var editor = _host.User(DashboardRoles.Editor, out var editorId);
        var (posted, body) = await PostV2Async(editor, id);
        posted.Dispose();

        var (again, problem) = await PostV2Async(editor, id);

        using (again)
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("already-posted", problem.GetProperty("type").GetString());
            Assert.Equal(body.GetProperty("postId").GetString(), problem.GetProperty("postId").GetString());
            Assert.Equal(body.GetProperty("attemptId").GetInt64(), problem.GetProperty("attemptId").GetInt64());
            Assert.Equal(editorId, problem.GetProperty("startedBy").GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.String, problem.GetProperty("startedUtc").ValueKind);
        }

        Assert.Single(Gateway.Writes);
        Assert.Single(await AttemptsAsync(id));
    }

    [Fact]
    public async Task Should_AnswerNullAttempt_When_AlreadyPostedBySettlement()
    {
        var id = await SaveV2Async(SyncPostFixtures.Read(SyncPostFixtures.PostedOneRow));
        Store.Change(id, r => r with { Status = SavedRequestStatus.Posted, PostId = "api-settled#1" });
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, problem) = await PostV2Async(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("already-posted", problem.GetProperty("type").GetString());
            Assert.Equal("api-settled#1", problem.GetProperty("postId").GetString());
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("attemptId").ValueKind);
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("startedBy").ValueKind);
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("startedUtc").ValueKind);
        }

        Assert.Empty(Gateway.Requests);
    }

    // ---- partial (spec 10 item 4.11, 13.2) ------------------------------------------------------------------------

    [Fact]
    public async Task Should_EndPartial_When_GatewayRejectsSomeRows()
    {
        var id = await SaveV2Async(V2Fixture("three-rows-with-email.json"));
        Gateway.RejectLine = 2;
        using var editor = _host.User(DashboardRoles.Editor);

        var (response, body) = await PostV2Async(editor, id);

        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("partial", body.GetProperty("status").GetString());
            Assert.Equal("partial", body.GetProperty("savedRequestStatus").GetString());
        }

        Assert.Equal(SavedRequestStatus.Partial, (await StoredAsync(id)).Status);
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Partial, attempt.Outcome);
        var detail = await DetailV2Async(id);
        Assert.Equal(2, detail.GetProperty("postSummary").GetProperty("postedCount").GetInt32());
        Assert.Single(Gateway.Writes);
    }

    // ---- long post (spec 13.2) -------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_FinishAndRecord_When_ClientDisconnects()
    {
        var id = await SaveV2Async(V2Fixture("post-false.json"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gateway.WriteGate = release.Task;
        var probe = new AbortProbe();
        using var host = _host.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(probe)));
        using var editor = host.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.Factory.Users.TokenFor(DashboardRoles.Editor));
        using var cancel = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{V2Route}/{id}/post");

        var call = editor.SendAsync(request, cancel.Token);
        await WaitUntilAsync(() => Gateway.Writes.Count == 1);
        cancel.Cancel();
        // The server has seen the disconnect while the qbXML is still with QuickBooks.
        await probe.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(SavedRequestStatus.Posting, (await StoredAsync(id)).Status);
        release.SetResult();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await StoredAsync(id)).Status == SavedRequestStatus.Posting)
        {
            Assert.True(DateTime.UtcNow < deadline, "the post never finished");
            await Task.Delay(10);
        }

        Assert.Equal(SavedRequestStatus.Posted, (await StoredAsync(id)).Status);
        var attempt = Assert.Single(await AttemptsAsync(id));
        Assert.Equal(AttemptOutcome.Posted, attempt.Outcome);
        Assert.Equal(200, attempt.HttpStatus);
        Assert.NotNull(attempt.Result);
        Assert.Equal("not-to-post", attempt.Result!["skipped"]![0]!["reason"]!.GetValue<string>());
        Assert.Single(Gateway.Writes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    /// <summary>
    /// Spec 13.2, through a real Kestrel socket. Kestrel's own timers (keep-alive 130 s, request headers 30 s) cover an
    /// idle connection and the receipt of headers, never a request the app is still answering. To show that without a
    /// 130 s wait, both are shrunk to 1 s here and the post is held mid-send for several times that: it still answers
    /// 200 and records its attempt. The two tests below pin that the shipped host keeps those defaults and adds no
    /// request-timeout policy, which is what makes this hold stand for the real one.
    /// </summary>
    [Fact]
    public async Task Should_NotTimeOut_When_PostOutlastsKestrelDefaults()
    {
        var id = await SaveV2Async(SyncPostFixtures.Read(SyncPostFixtures.PostedOneRow));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gateway.WriteGate = release.Task;
        var pipeline = new PipelineCapture();
        using var app = _host.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(pipeline)));
        _ = app.Server; // builds the app's pipeline
        var options = new KestrelServerOptions { ApplicationServices = app.Services, AddServerHeader = false };
        options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(1);
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(1);
        options.Listen(IPAddress.Loopback, 0);
        using var kestrel = new KestrelServer(
            Options.Create(options),
            new SocketTransportFactory(Options.Create(new SocketTransportOptions()), NullLoggerFactory.Instance),
            NullLoggerFactory.Instance);
        await kestrel.StartAsync(
            new PipelineApplication(pipeline.Pipeline!, app.Services.GetRequiredService<IHttpContextFactory>()), CancellationToken.None);
        try
        {
            var address = Assert.Single(kestrel.Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _host.Factory.Users.TokenFor(DashboardRoles.Editor));

            var call = client.PostAsync($"{V2Route}/{id}/post", null);
            await WaitUntilAsync(() => Gateway.Writes.Count == 1);
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.False(call.IsCompleted, "the post answered before QuickBooks did");
            release.SetResult();
            using var response = await call.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await SavedRequestTestHost.BodyOf(response);
            Assert.Equal("posted", body.GetProperty("savedRequestStatus").GetString());
            var attempt = Assert.Single(await AttemptsAsync(id));
            Assert.Equal(AttemptOutcome.Posted, attempt.Outcome);
            Assert.Equal(body.GetProperty("attemptId").GetInt64(), attempt.Id);
        }
        finally
        {
            release.TrySetResult();
            await kestrel.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Should_KeepKestrelTimeoutDefaults_When_HostConfigured()
    {
        var shipped = _host.Factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        var defaults = new KestrelServerOptions().Limits;

        Assert.Equal(defaults.KeepAliveTimeout, shipped.KeepAliveTimeout);
        Assert.Equal(defaults.RequestHeadersTimeout, shipped.RequestHeadersTimeout);
        Assert.Equal(defaults.MinResponseDataRate?.BytesPerSecond, shipped.MinResponseDataRate?.BytesPerSecond);
        Assert.Equal(defaults.MinResponseDataRate?.GracePeriod, shipped.MinResponseDataRate?.GracePeriod);
        Assert.Equal(defaults.MinRequestBodyDataRate?.BytesPerSecond, shipped.MinRequestBodyDataRate?.BytesPerSecond);
        Assert.Equal(defaults.MinRequestBodyDataRate?.GracePeriod, shipped.MinRequestBodyDataRate?.GracePeriod);
    }

    [Fact]
    public void Should_RegisterNoRequestTimeout_When_HostBuilt()
    {
        var services = _host.Factory.Services;
        var timeouts = services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;
        Assert.Null(timeouts.DefaultPolicy);
        Assert.Empty(timeouts.Policies);

        var posts = services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.EndsWith("/post", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(posts);
        Assert.All(posts, e =>
        {
            Assert.Null(e.Metadata.GetMetadata<RequestTimeoutAttribute>());
            Assert.Null(e.Metadata.GetMetadata<RequestTimeoutPolicy>());
        });
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

    /// <summary>Keeps the app's built pipeline, so a real Kestrel server can serve it beside the test server.</summary>
    private sealed class PipelineCapture : IStartupFilter
    {
        public RequestDelegate? Pipeline { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(rest =>
            {
                Pipeline = rest;
                return rest;
            });
            next(app);
        };
    }

    /// <summary>The app's pipeline as a Kestrel application: one context per request, from the app's own factory.</summary>
    private sealed class PipelineApplication(RequestDelegate pipeline, IHttpContextFactory contexts) : IHttpApplication<HttpContext>
    {
        public HttpContext CreateContext(IFeatureCollection contextFeatures) => contexts.Create(contextFeatures);

        public Task ProcessRequestAsync(HttpContext context) => pipeline(context);

        public void DisposeContext(HttpContext context, Exception? exception) => contexts.Dispose(context);
    }
}
