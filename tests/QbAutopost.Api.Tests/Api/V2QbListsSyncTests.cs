using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Endpoints.V2;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Security;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// M14 §7 (R-16), acceptance A-2, A-3, A-4: <c>GET /api/v2/quickbooks/lists</c> with <c>lastSync</c> and
/// <c>nextSyncUtc</c>, and the dashboard's <c>POST /api/v2/quickbooks/lists/sync</c>: scope, presence guard, the single
/// flight shared with the scheduled sync, the QuickBooks problem mapping, and the wait bound.
/// </summary>
public sealed class V2QbListsSyncTests : IDisposable
{
    private const string Lists = ApiRoutes.V2Prefix + "/quickbooks/lists";
    private const string Sync = Lists + "/sync";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly ApiFactory _factory = new();
    private readonly string _readOnlyKey = ApiClientStore.NewKey();
    private readonly string _noQbKey = ApiClientStore.NewKey();
    private readonly TaskCompletionSource _queryGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CapturingLoggerProvider _logs = new();

    public V2QbListsSyncTests()
    {
        var company = _factory.Gateway.Company;
        foreach (var name in new[] { "Chase Checking 4521", "Utilities", "Repairs and Maintenance" })
        {
            company.Accounts.Add(new QbAccount { Name = name, Type = "Expense" });
        }

        company.Vendors.Add("Home Depot");
        company.Vendors.Add("Amazon");
        company.Customers.Add("Palm Court Rentals LLC");

        var file = _factory.Dir.Combine("data", "clients.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var reader = ApiClientStore.NewSecret(_readOnlyKey);
        var n8n = ApiClientStore.NewSecret(_noQbKey);
        new ApiClientStore(file).Save(new ApiClientList
        {
            Clients =
            [
                new ApiClient
                {
                    Id = "reader", Name = "List reader", KeyHash = reader.KeyHash, KeySalt = reader.KeySalt,
                    Scopes = [ApiScopes.QbRead], CreatedUtc = DateTime.UtcNow.AddDays(-1),
                },
                new ApiClient
                {
                    Id = "n8n", Name = "n8n workflow", KeyHash = n8n.KeyHash, KeySalt = n8n.KeySalt,
                    Scopes = [ApiScopes.RequestsSubmit, ApiScopes.ListsRead], CreatedUtc = DateTime.UtcNow.AddDays(-1),
                },
            ],
        });
    }

    public void Dispose()
    {
        _queryGate.TrySetResult();
        _factory.Dispose();
    }

    // ---- GET ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ShowNullLastSyncAndNextSync_When_NothingWasAttemptedAndTheScheduleIsOff()
    {
        using var viewer = User(_factory, DashboardRoles.Viewer);

        var body = await GetOkAsync(viewer, Lists);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastSync").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextSyncUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("syncedUtc").ValueKind);
        Assert.Empty(body.GetProperty("accounts").EnumerateArray());
    }

    [Fact]
    public async Task Should_ShowTheNextScheduledSync_When_TheScheduleIsOn()
    {
        using var host = _factory.WithSettings(new Dictionary<string, string?>
        {
            ["QuickBooks:ListSync:IntervalMinutes"] = "30",
            ["QuickBooks:ListSync:FirstDelayMinutes"] = "2",
        });
        using var viewer = User(host, DashboardRoles.Viewer);
        var before = DateTime.UtcNow;

        var body = await GetOkAsync(viewer, Lists);

        var next = body.GetProperty("nextSyncUtc").GetDateTime().ToUniversalTime();
        Assert.Equal(host.Services.GetRequiredService<ListSyncSchedule>().NextSyncUtc, next);
        Assert.InRange(next, before.AddMinutes(2).AddSeconds(-5), DateTime.UtcNow.AddMinutes(2).AddSeconds(5));
    }

    [Fact]
    public async Task Should_NameTheRequester_When_TheLastAttemptWasADashboardSync()
    {
        var token = _factory.Users.TokenFor(DashboardRoles.Editor);
        using var editor = Bearer(_factory, token);
        await PostOkAsync(editor);
        using var viewer = User(_factory, DashboardRoles.Viewer);

        var last = (await GetOkAsync(viewer, Lists)).GetProperty("lastSync");

        Assert.Equal("dashboard", last.GetProperty("trigger").GetString());
        Assert.Equal("ok", last.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("reason").ValueKind);
        var id = _factory.Users.IdOf(token);
        Assert.Equal(id, last.GetProperty("requestedBy").GetProperty("id").GetString());
        Assert.Equal("User " + id["user-".Length..], last.GetProperty("requestedBy").GetProperty("displayName").GetString());
        Assert.Equal(ListSyncMessages.For(Source(_factory).LastAttempt!), last.GetProperty("message").GetString());
        Assert.True(last.GetProperty("finishedUtc").GetDateTime() >= last.GetProperty("startedUtc").GetDateTime());
    }

    [Fact]
    public async Task Should_ShowNoRequester_When_TheLastAttemptWasNotADashboardSync()
    {
        using var key = Key(_factory, ApiFactory.ApiKey);
        using var v1 = await key.PostAsync(ApiRoutes.V1Prefix + "/quickbooks/lists/sync", null);
        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        using var viewer = User(_factory, DashboardRoles.Viewer);

        var last = (await GetOkAsync(viewer, Lists)).GetProperty("lastSync");

        Assert.Equal("api", last.GetProperty("trigger").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("requestedBy").ValueKind);
    }

    [Fact]
    public async Task Should_ShowTheIdAsTheName_When_TheRequesterIsUnknown()
    {
        using var viewer = User(_factory, DashboardRoles.Viewer);
        await Store(_factory).RecordAsync(
            new ListSyncRun
            {
                Trigger = ListSyncTrigger.Dashboard, RequestedBy = "ghost-user", Outcome = ListSyncOutcome.Skipped,
                Reason = ListSyncReasons.QuickBooksNotOpen, StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow,
            },
            null,
            CancellationToken.None);

        var requester = (await GetOkAsync(viewer, Lists)).GetProperty("lastSync").GetProperty("requestedBy");

        Assert.Equal("ghost-user", requester.GetProperty("id").GetString());
        Assert.Equal("ghost-user", requester.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Should_KeepTheV1ListsBody_When_TheV1RouteIsRead()
    {
        using var key = Key(_factory, ApiFactory.ApiKey);
        using var response = await key.GetAsync(ApiRoutes.V1Prefix + "/quickbooks/lists");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["syncedUtc", "accounts", "vendors", "customers"], body.EnumerateObject().Select(p => p.Name));
    }

    // ---- POST: success and scope (A-4) -------------------------------------------------------------------------------

    [Theory]
    [InlineData(DashboardRoles.Editor)]
    [InlineData(DashboardRoles.Admin)]
    public async Task Should_Answer200WithFreshListsAndCounts_When_AnEditorOrAdminSyncs(string role)
    {
        using var client = User(_factory, role);

        var body = await PostOkAsync(client);

        Assert.Equal(
            ["syncedUtc", "accounts", "vendors", "customers", "lastSync", "nextSyncUtc", "counts"],
            body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["Chase Checking 4521", "Utilities", "Repairs and Maintenance"], body.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["Home Depot", "Amazon"], body.GetProperty("vendors").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(3, body.GetProperty("counts").GetProperty("accounts").GetInt32());
        Assert.Equal(2, body.GetProperty("counts").GetProperty("vendors").GetInt32());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("customers").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("syncedUtc").ValueKind);
        Assert.Equal("dashboard", body.GetProperty("lastSync").GetProperty("trigger").GetString());
        Assert.Equal("ok", body.GetProperty("lastSync").GetProperty("outcome").GetString());
        Assert.False(body.TryGetProperty("missingInRules", out _));
        Assert.Single(_factory.Gateway.Queries);
        Assert.Empty(_factory.Gateway.Writes);
    }

    [Fact]
    public async Task Should_Answer403AndNotSync_When_AViewerSyncs()
    {
        using var viewer = User(_factory, DashboardRoles.Viewer);

        using var response = await viewer.PostAsync(Sync, null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Null(Source(_factory).LastAttempt);
    }

    [Fact]
    public async Task Should_Answer403_When_AnApiKeyWithoutQbReadSyncs()
    {
        using var key = Key(_factory, _noQbKey);

        using var response = await key.PostAsync(Sync, null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_SyncAsAnApiCaller_When_AnApiKeyWithQbReadSyncs()
    {
        using var key = Key(_factory, _readOnlyKey);

        var body = await PostOkAsync(key);

        Assert.Equal("api", body.GetProperty("lastSync").GetProperty("trigger").GetString());
        Assert.Equal("List reader", Source(_factory).LastAttempt!.RequestedBy);
    }

    [Fact]
    public void Should_NeedRequestsEditOrQbRead_When_TheV2SyncIsCalled()
    {
        Assert.Equal(ApiScopes.AnyOf(ApiScopes.RequestsEdit, ApiScopes.QbRead), ApiRoutes.ScopeFor("POST", Sync));
        Assert.Equal(ApiScopes.QbRead, ApiRoutes.ScopeFor("POST", ApiRoutes.V1Prefix + "/quickbooks/lists/sync"));
        Assert.Equal(ApiScopes.QbRead, ApiRoutes.ScopeFor("POST", "/quickbooks/lists/sync"));
    }

    [Fact]
    public void Should_UseThePostingBucket_When_TheV2SyncIsCalled()
    {
        Assert.Equal(RateLimitBucket.Post, RateLimitPolicy.BucketFor("POST", new PathString(Sync)));
        Assert.Equal(RateLimitBucket.Default, RateLimitPolicy.BucketFor("POST", new PathString(ApiRoutes.V1Prefix + "/quickbooks/lists/sync")));
        Assert.Equal(RateLimitBucket.Default, RateLimitPolicy.BucketFor("POST", new PathString("/qb/sync-lists")));
        Assert.Equal(RateLimitBucket.Default, RateLimitPolicy.BucketFor("GET", new PathString(Lists)));
    }

    /// <summary>Routing matches paths case-insensitively, so the scope and the bucket must too.</summary>
    [Theory]
    [InlineData("/api/v2/QuickBooks/Lists/Sync")]
    [InlineData("/API/V2/QUICKBOOKS/LISTS/SYNC/")]
    public void Should_UseThePostingBucketAndRequestsEditScope_When_TheV2SyncPathIsMixedCase(string path)
    {
        Assert.Equal(RateLimitBucket.Post, RateLimitPolicy.BucketFor("POST", new PathString(path)));
        Assert.Equal(ApiScopes.AnyOf(ApiScopes.RequestsEdit, ApiScopes.QbRead), ApiRoutes.ScopeFor("POST", path));
    }

    [Fact]
    public async Task Should_NotWriteAnAuditFeedEvent_When_ADashboardUserSyncs()
    {
        using var admin = User(_factory, DashboardRoles.Admin);
        await PostOkAsync(admin);

        var feed = await GetOkAsync(admin, ApiRoutes.V2Prefix + "/audit-events");

        Assert.Equal(0, feed.GetProperty("total").GetInt32());
    }

    // ---- POST: guard (A-2) and failures (A-3) ------------------------------------------------------------------------

    [Fact]
    public async Task Should_Answer409AndRecordSkipped_When_QuickBooksIsNotOpen()
    {
        var presence = new FakeQbPresence { IsOpen = false };
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => Replace<IQbPresence>(s, presence)));
        var token = _factory.Users.TokenFor(DashboardRoles.Editor);
        using var editor = Bearer(host, token);

        using var response = await editor.PostAsync(Sync, null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal(QuickBooksListsV2Endpoints.NotOpenType, problem.GetProperty("type").GetString());
        Assert.Equal("QuickBooks isn't open", problem.GetProperty("title").GetString());
        Assert.Equal("Open QuickBooks on the company file, then sync again.", problem.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Equal(1, presence.Calls);
        var row = Source(host).LastAttempt!;
        Assert.Equal((ListSyncTrigger.Dashboard, ListSyncOutcome.Skipped, ListSyncReasons.QuickBooksNotOpen), (row.Trigger, row.Outcome, row.Reason));
        Assert.Equal(_factory.Users.IdOf(token), row.RequestedBy);

        var last = (await GetOkAsync(editor, Lists)).GetProperty("lastSync");
        Assert.Equal("skipped", last.GetProperty("outcome").GetString());
        Assert.Equal(ListSyncReasons.QuickBooksNotOpen, last.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Should_Answer503KeepTheListsAndRecordFailed_When_QuickBooksIsUnavailable()
    {
        using var editor = User(_factory, DashboardRoles.Editor);
        var first = await PostOkAsync(editor);
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("no session to C:\\secret\\path.QBW");

        using var response = await editor.PostAsync(Sync, null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("QuickBooks is not available", problem.GetProperty("title").GetString());
        var row = Source(_factory).LastAttempt!;
        Assert.Equal((ListSyncOutcome.Failed, ListSyncReasons.QuickBooksUnavailable), (row.Outcome, row.Reason));

        var body = await GetOkAsync(editor, Lists);
        Assert.Equal(first.GetProperty("syncedUtc").GetString(), body.GetProperty("syncedUtc").GetString());
        Assert.Equal(3, body.GetProperty("accounts").GetArrayLength());
        var last = body.GetProperty("lastSync");
        Assert.Equal("failed", last.GetProperty("outcome").GetString());
        Assert.Equal(ListSyncReasons.QuickBooksUnavailable, last.GetProperty("reason").GetString());
        Assert.DoesNotContain("secret", last.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("no session", last.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Answer502_When_QuickBooksRefusesTheQuery()
    {
        using var editor = User(_factory, DashboardRoles.Editor);
        _factory.Gateway.QueryThrow = new QbStatusException("status 3100");

        using var response = await editor.PostAsync(Sync, null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("QuickBooks refused the request", (await response.ReadProblemAsync()).GetProperty("title").GetString());
        Assert.Equal(ListSyncReasons.QbStatus, Source(_factory).LastAttempt!.Reason);
    }

    // ---- POST: single flight (A-4, review focus 5) -------------------------------------------------------------------

    [Fact]
    public async Task Should_CallQuickBooksOnceAndAnswerBothAlike_When_TwoEditorsSyncTogether()
    {
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.AddProvider(_logs)))));
        _factory.Gateway.QueryGate = _queryGate.Task;
        using var first = User(host, DashboardRoles.Editor);
        using var second = User(host, DashboardRoles.Editor);

        var a = first.PostAsync(Sync, null);
        await UntilAsync(() => _factory.Gateway.Requests.Count == 1);
        var b = second.PostAsync(Sync, null);
        await UntilJoinedAsync();
        Assert.False(a.IsCompleted || b.IsCompleted);
        _queryGate.TrySetResult();

        using var ra = await a.WaitAsync(Patience);
        using var rb = await b.WaitAsync(Patience);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (ra.StatusCode, rb.StatusCode));
        var lastA = (await ra.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lastSync").GetRawText();
        var lastB = (await rb.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lastSync").GetRawText();
        Assert.Equal(lastA, lastB);
        Assert.Single(_factory.Gateway.Queries);
        Assert.Null(host.Services.GetRequiredService<ListSyncSchedule>().InFlight);
    }

    [Fact]
    public async Task Should_JoinTheScheduledSync_When_ItIsInFlight()
    {
        _factory.Gateway.QueryGate = _queryGate.Task;
        using var host = ScheduleDueAtOnce();
        using var editor = User(host, DashboardRoles.Editor);
        await UntilAsync(() => _factory.Gateway.Requests.Count == 1);
        var schedule = host.Services.GetRequiredService<ListSyncSchedule>();
        Assert.NotNull(schedule.InFlight);

        var post = editor.PostAsync(Sync, null);
        await UntilJoinedAsync();
        Assert.False(post.IsCompleted);
        _queryGate.TrySetResult();

        using var response = await post.WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("schedule", body.GetProperty("lastSync").GetProperty("trigger").GetString());
        Assert.Equal(3, body.GetProperty("counts").GetProperty("accounts").GetInt32());
        Assert.Single(_factory.Gateway.Queries);
        Assert.Equal(ListSyncTrigger.Schedule, Source(host).LastAttempt!.Trigger);
    }

    [Fact]
    public async Task Should_AnswerWithTheQuickBooksProblem_When_TheJoinedScheduledSyncFails()
    {
        _factory.Gateway.QueryGate = _queryGate.Task;
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("no session");
        using var host = ScheduleDueAtOnce();
        using var editor = User(host, DashboardRoles.Editor);
        await UntilAsync(() => _factory.Gateway.Requests.Count == 1);

        var post = editor.PostAsync(Sync, null);
        await UntilJoinedAsync();
        _queryGate.TrySetResult();

        using var response = await post.WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // The gateway retries a failed read once; neither call was the dashboard's own sync.
        Assert.DoesNotContain(_logs.Entries, e => e.Entry.Message.StartsWith("QuickBooks lists sync (dashboard, by user-", StringComparison.Ordinal)
                                                  && !e.Entry.Message.Contains("joined", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Entries, e => e.Entry.Message.Contains("Job qb-sync-lists: Exclusive started", StringComparison.Ordinal));
        Assert.Equal((ListSyncTrigger.Schedule, ListSyncOutcome.Failed), (Source(host).LastAttempt!.Trigger, Source(host).LastAttempt!.Outcome));
    }

    [Fact]
    public async Task Should_Answer409_When_TheJoinedScheduledAttemptIsSkipped()
    {
        var presence = new SequencePresence(false); // the scheduled attempt finds QuickBooks closed; the request finds it open
        GatedStore? gated = null;
        using var host = ScheduleDueAtOnce(s =>
        {
            Replace<IQbPresence>(s, presence);
            s.RemoveAll<IQbListsStore>();
            s.AddSingleton<IQbListsStore>(sp => gated = new GatedStore(sp.GetRequiredService<FileQbLists>(), ListSyncOutcome.Skipped));
        });
        using var editor = User(host, DashboardRoles.Editor);
        await UntilAsync(() => gated?.Entered.Task.IsCompleted == true);

        var post = editor.PostAsync(Sync, null);
        await UntilJoinedAsync();
        Assert.Equal(2, presence.Calls);
        Assert.False(post.IsCompleted);
        gated!.Release.TrySetResult();

        using var response = await post.WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(QuickBooksListsV2Endpoints.NotOpenType, (await response.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Equal(ListSyncTrigger.Schedule, Source(host).LastAttempt!.Trigger);
    }

    // ---- POST: the wait bound -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_Answer504AndLetTheSyncFinish_When_ItOutlastsTheWait()
    {
        _factory.Gateway.QueryGate = _queryGate.Task;
        using var host = _factory.WithSetting("Api:SyncPostTimeoutSeconds", "1");
        using var editor = User(host, DashboardRoles.Editor);

        using var response = await editor.PostAsync(Sync, null).WaitAsync(Patience);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal(QuickBooksListsV2Endpoints.TimeoutType, problem.GetProperty("type").GetString());
        Assert.Null(Source(host).LastAttempt);

        _queryGate.TrySetResult();
        await UntilAsync(() => Source(host).LastAttempt is not null);
        var last = (await GetOkAsync(editor, Lists)).GetProperty("lastSync");
        Assert.Equal("ok", last.GetProperty("outcome").GetString());
        Assert.Single(_factory.Gateway.Queries);
    }

    // ---- the single-flight handle -------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_StartOneFlightAndJoinIt_When_TwoCallersAskTogether()
    {
        var schedule = NewSchedule();
        var enqueued = new List<Func<CancellationToken, Task<ListSyncCompletion>>>();
        var result = Done(1);

        var (first, joinedFirst) = schedule.JoinOrStart(_ => Task.FromResult(result), enqueued.Add);
        var (second, joinedSecond) = schedule.JoinOrStart(_ => throw new InvalidOperationException("never started"), enqueued.Add);

        Assert.Equal((false, true), (joinedFirst, joinedSecond));
        Assert.Same(first, second);
        Assert.Same(first, schedule.InFlight);
        Assert.Single(enqueued);
        Assert.False(first.IsCompleted);

        await enqueued[0](CancellationToken.None);

        Assert.Same(result, await first);
        Assert.Null(schedule.InFlight);
    }

    [Fact]
    public async Task Should_FaultTheFlightAndClearIt_When_TheSyncFails()
    {
        var schedule = NewSchedule();
        Func<CancellationToken, Task<ListSyncCompletion>>? work = null;
        var (flight, _) = schedule.JoinOrStart(_ => throw new QuickBooksBusyException("busy"), w => work = w);

        await Assert.ThrowsAsync<QuickBooksBusyException>(() => work!(CancellationToken.None));

        await Assert.ThrowsAsync<QuickBooksBusyException>(() => flight);
        Assert.Null(schedule.InFlight);
    }

    [Fact]
    public void Should_LeaveNothingInFlight_When_TheWorkCannotBeQueued()
    {
        var schedule = NewSchedule();

        Assert.Throws<InvalidOperationException>(() =>
            schedule.JoinOrStart(_ => Task.FromResult(Done(0)), _ => throw new InvalidOperationException("closed")));

        Assert.Null(schedule.InFlight);
    }

    /// <summary>Review fix 5: the two triggers exclude each other through the one handle, with no worker or host.</summary>
    [Fact]
    public async Task Should_RefuseAScheduledAttempt_When_ADashboardFlightHoldsTheHandle()
    {
        var schedule = NewSchedule();
        Func<CancellationToken, Task<ListSyncCompletion>>? work = null;
        var (flight, _) = schedule.JoinOrStart(_ => Task.FromResult(Done(4)), w => work = w);

        Assert.Null(schedule.BeginAttempt());

        await work!(CancellationToken.None);
        Assert.True(flight.IsCompletedSuccessfully);
        var attempt = schedule.BeginAttempt();

        Assert.NotNull(attempt);
        Assert.Same(attempt.Task, schedule.InFlight);
        var (joined, wasJoined) = schedule.JoinOrStart(_ => throw new InvalidOperationException("never started"), _ => Assert.Fail("never queued"));
        Assert.True(wasJoined);
        Assert.Same(attempt.Task, joined);
        attempt.TrySetResult(null);
        schedule.EndAttempt(ListSyncOutcome.Skipped);
        Assert.Null(schedule.InFlight);
    }

    // ---- review fixes: the flight's own answer, v2 codes, stopping -----------------------------------------------------

    [Fact]
    public async Task Should_AnswerWithTheRunAndListsTheFlightProduced_When_TheSourceHasMovedOn()
    {
        using var host = LoggedHost();
        using var editor = User(host, DashboardRoles.Editor);
        var schedule = host.Services.GetRequiredService<ListSyncSchedule>();
        var attempt = schedule.BeginAttempt()!;
        var post = editor.PostAsync(Sync, null);
        await UntilJoinedAsync();

        // Something else is recorded after the flight's own run; the answer must still be the flight's.
        await Store(host).RecordAsync(Skipped(), null, CancellationToken.None);
        var syncedUtc = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
        var lists = new QbLists
        {
            SyncedUtc = syncedUtc,
            Accounts = [new QbAccount { Name = "Flight Account", Type = "Bank" }],
            Vendors = ["Flight Vendor"],
            Customers = [],
        };
        var run = new ListSyncRun
        {
            Trigger = ListSyncTrigger.Schedule, Outcome = ListSyncOutcome.Ok, Accounts = 1, Vendors = 1, Customers = 0,
            StartedUtc = syncedUtc, FinishedUtc = syncedUtc,
        };
        attempt.TrySetResult(new ListSyncCompletion(new ListSyncResult(1, 1, 0, []), run, lists));

        using var response = await post.WaitAsync(Patience);
        schedule.EndAttempt(ListSyncOutcome.Ok);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["Flight Account"], body.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["Flight Vendor"], body.GetProperty("vendors").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(syncedUtc, body.GetProperty("syncedUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("schedule", body.GetProperty("lastSync").GetProperty("trigger").GetString());
        Assert.Equal("ok", body.GetProperty("lastSync").GetProperty("outcome").GetString());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("accounts").GetInt32());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("vendors").GetInt32());
        Assert.Equal(0, body.GetProperty("counts").GetProperty("customers").GetInt32());
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer503ApiStopping_When_TheJoinedFlightIsCancelled()
    {
        using var host = LoggedHost();
        using var editor = User(host, DashboardRoles.Editor);
        var schedule = host.Services.GetRequiredService<ListSyncSchedule>();
        var attempt = schedule.BeginAttempt()!;
        var post = editor.PostAsync(Sync, null);
        await UntilJoinedAsync();

        attempt.TrySetCanceled();

        using var response = await post.WaitAsync(Patience);
        schedule.EndAttempt(ListSyncOutcome.Failed);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal(QuickBooksListsV2Endpoints.StoppingType, problem.GetProperty("type").GetString());
        Assert.Equal("The API is stopping; try again in a moment.", problem.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    public static TheoryData<string, HttpStatusCode, string> QuickBooksFailures() => new()
    {
        { "unavailable", HttpStatusCode.ServiceUnavailable, ListSyncReasons.QuickBooksUnavailable },
        { "call", HttpStatusCode.ServiceUnavailable, ListSyncReasons.QuickBooksUnavailable },
        { "busy", HttpStatusCode.ServiceUnavailable, ListSyncReasons.QuickBooksBusy },
        { "status", HttpStatusCode.BadGateway, ListSyncReasons.QbStatus },
        { "format", HttpStatusCode.BadGateway, ListSyncReasons.QbStatus },
    };

    [Theory]
    [MemberData(nameof(QuickBooksFailures))]
    public async Task Should_AnswerTheV2CodeWithAFixedDetail_When_QuickBooksFails(string failure, HttpStatusCode status, string code)
    {
        const string Raw = "RAW-ERROR-TEXT at C:\\secret\\company.QBW";
        _factory.Gateway.QueryThrow = failure switch
        {
            "unavailable" => new QuickBooksUnavailableException(Raw),
            "call" => new QuickBooksCallException(Raw, -1, null),
            "busy" => new QuickBooksBusyException(Raw),
            "status" => new QbStatusException(Raw),
            _ => new FormatException(Raw),
        };
        using var editor = User(_factory, DashboardRoles.Editor);

        using var response = await editor.PostAsync(Sync, null);

        Assert.Equal(status, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal(code, problem.GetProperty("type").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.DoesNotContain("RAW-ERROR-TEXT", problem.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", detail, StringComparison.Ordinal);
        Assert.Equal(code, Source(_factory).LastAttempt!.Reason);
        Assert.Contains("RAW-ERROR-TEXT", Source(_factory).LastAttempt!.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_KeepTheV1RawProblem_When_TheV1SyncFails()
    {
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("RAW-ERROR-TEXT");
        using var key = Key(_factory, ApiFactory.ApiKey);

        using var response = await key.PostAsync(ApiRoutes.V1Prefix + "/quickbooks/lists/sync", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains("RAW-ERROR-TEXT", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(ListSyncReasons.QuickBooksUnavailable, problem.GetProperty("type").GetString());
    }

    // ---- messages -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ListSyncOutcome.Ok, null)]
    [InlineData(ListSyncOutcome.Skipped, ListSyncReasons.QuickBooksNotOpen)]
    [InlineData(ListSyncOutcome.Skipped, ListSyncReasons.WorkerBusy)]
    [InlineData(ListSyncOutcome.Failed, ListSyncReasons.QuickBooksUnavailable)]
    [InlineData(ListSyncOutcome.Failed, ListSyncReasons.QuickBooksBusy)]
    [InlineData(ListSyncOutcome.Failed, ListSyncReasons.QbStatus)]
    [InlineData(ListSyncOutcome.Failed, ListSyncReasons.RulesUnreadable)]
    [InlineData(ListSyncOutcome.Failed, null)]
    public void Should_NeverShowTheRawError_When_AMessageIsChosen(ListSyncOutcome outcome, string? reason)
    {
        var run = new ListSyncRun
        {
            Trigger = ListSyncTrigger.Schedule, Outcome = outcome, Reason = reason, Error = "boom at C:\\data\\secret.QBW",
            StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow,
        };

        var message = ListSyncMessages.For(run);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("boom", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_GiveEachReasonItsOwnMessage_When_MessagesAreChosen()
    {
        string For(ListSyncOutcome outcome, string? reason) => ListSyncMessages.For(new ListSyncRun
        {
            Trigger = ListSyncTrigger.Schedule, Outcome = outcome, Reason = reason, StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow,
        });

        var messages = new[]
        {
            For(ListSyncOutcome.Ok, null),
            For(ListSyncOutcome.Skipped, ListSyncReasons.QuickBooksNotOpen),
            For(ListSyncOutcome.Failed, ListSyncReasons.QuickBooksUnavailable),
            For(ListSyncOutcome.Failed, ListSyncReasons.QuickBooksBusy),
            For(ListSyncOutcome.Failed, ListSyncReasons.QbStatus),
            For(ListSyncOutcome.Failed, ListSyncReasons.RulesUnreadable),
        };

        Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("not open", messages[1], StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A host whose scheduled sync is due the moment the worker starts.</summary>
    private WebApplicationFactory<Program> ScheduleDueAtOnce(Action<IServiceCollection>? services = null) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(s => s.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.AddProvider(_logs))));
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QuickBooks:ListSync:IntervalMinutes"] = "5",
                ["QuickBooks:ListSync:FirstDelayMinutes"] = "0",
            }));
            if (services is not null)
            {
                b.ConfigureTestServices(services);
            }
        });

    private ListSyncSchedule NewSchedule() =>
        new(new global::QbAutopost.Api.Configuration.ListSyncSettings { IntervalMinutes = 0 }, new FileQbLists(_factory.Dir.Combine("unit-lists.json")), new FixedClock());

    private WebApplicationFactory<Program> LoggedHost() =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.AddProvider(_logs)))));

    private static ListSyncCompletion Done(int accounts) =>
        new(new ListSyncResult(accounts, 0, 0, []), Skipped() with { Outcome = ListSyncOutcome.Ok, Reason = null }, QbLists.Empty);

    private static ListSyncRun Skipped() => new()
    {
        Trigger = ListSyncTrigger.Api, Outcome = ListSyncOutcome.Skipped, Reason = ListSyncReasons.QuickBooksNotOpen,
        StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow,
    };

    private static IQbListsSource Source(WebApplicationFactory<Program> host) => host.Services.GetRequiredService<IQbListsSource>();

    private static IQbListsStore Store(WebApplicationFactory<Program> host) => host.Services.GetRequiredService<IQbListsStore>();

    private static void Replace<T>(IServiceCollection services, T instance)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    private HttpClient User(WebApplicationFactory<Program> host, string role) => Bearer(host, _factory.Users.TokenFor(role));

    private static HttpClient Bearer(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpClient Key(WebApplicationFactory<Program> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PostOkAsync(HttpClient client)
    {
        using var response = await client.PostAsync(Sync, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(10);
        }
    }

    private Task UntilJoinedAsync() =>
        UntilAsync(() => _logs.Entries.Any(e => e.Entry.Message.Contains("joined the sync in flight", StringComparison.Ordinal)));

    /// <summary>Answers the given values in turn, then open.</summary>
    private sealed class SequencePresence(params bool[] answers) : IQbPresence
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool IsCompanyOpen()
        {
            var n = Interlocked.Increment(ref _calls);
            return n > answers.Length || answers[n - 1];
        }
    }

    /// <summary>The file store, with a record of one outcome held until released (a slow database).</summary>
    private sealed class GatedStore(FileQbLists inner, ListSyncOutcome hold) : IQbListsStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RecordAsync(ListSyncRun run, QbLists? lists, CancellationToken ct)
        {
            if (run.Outcome == hold)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }

            await inner.RecordAsync(run, lists, ct);
        }

        public Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct) => inner.LastAttemptAsync(ct);

        public Task<ListSyncPage> ListAttemptsAsync(ListSyncQuery query, CancellationToken ct) => inner.ListAttemptsAsync(query, ct);
    }
}
