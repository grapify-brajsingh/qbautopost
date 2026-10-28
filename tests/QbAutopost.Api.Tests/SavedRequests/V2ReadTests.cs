using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1111 (dashboard v2 spec 9, 6.4, 10 items 4.1, 4.3, 4.8, 4.9): <c>GET /api/v2/saved-requests</c>, <c>/stats</c>,
/// <c>/{id}</c> and <c>/{id}/attempts/{attemptId}</c>. Each answers with the v2 judgement and the v2 fields; the v1
/// routes keep their shapes (their own tests are unedited).
/// </summary>
public sealed class V2ReadTests : IDisposable
{
    private const string V2Route = "/api/v2/saved-requests";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private string BatchesRoot => _host.Factory.Dir.Combine("data", "api-batches");

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

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        using var viewer = _host.User(DashboardRoles.Viewer);
        using var response = await viewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    private Task<JsonElement> DetailAsync(long id) => GetJsonAsync($"{V2Route}/{id}");

    private async Task<JsonElement> RowAsync(long id) =>
        (await GetJsonAsync($"{V2Route}?pageSize=100")).GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("id").GetInt64() == id);

    private async Task PostAsync(long id)
    {
        using var editor = _host.User(DashboardRoles.Editor);
        using var response = await editor.PostAsync($"{V2Route}/{id}/post", null);
    }

    /// <summary>Saves a fixture and posts it with QuickBooks failing, so the request ends <c>unknown</c>.</summary>
    private async Task<long> UnknownAsync()
    {
        var id = await SaveV2Async(SyncPostFixtures.Read(SyncPostFixtures.NoResponse));
        _host.Factory.Gateway.Throw = new QuickBooksCallException("ProcessRequest failed (0x80010105)", unchecked((int)0x80010105));
        await PostAsync(id);
        _host.Factory.Gateway.Throw = null;
        Assert.Equal(SavedRequestStatus.Unknown, (await Store.GetAsync(id, CancellationToken.None))!.Status);
        return id;
    }

    [Fact]
    public async Task Should_ReturnEtag_When_DetailRead()
    {
        var id = await SaveV2Async(V2Fixture("with-email.json"));
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync($"{V2Route}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rowVersion = (await SavedRequestTestHost.BodyOf(response)).GetProperty("rowVersion").GetInt32();
        Assert.Equal($"\"{rowVersion}\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Should_ShowValidationSummaryNone_When_NeverValidated()
    {
        var id = await SaveV2Async(V2Fixture("with-email.json"));
        Store.Change(id, r => r with { ValidationV2 = null, ProblemCount = 0 });

        var summary = (await DetailAsync(id)).GetProperty("validationSummary");
        var rowSummary = (await RowAsync(id)).GetProperty("validationSummary");

        foreach (var s in new[] { summary, rowSummary })
        {
            Assert.Equal("none", s.GetProperty("state").GetString());
            Assert.Equal(0, s.GetProperty("problemCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, s.GetProperty("validatedUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, s.GetProperty("versionNumber").ValueKind);
        }
    }

    [Fact]
    public async Task Should_ShowProblemCount_When_Held()
    {
        var id = await SaveV2Async(SyncPostFixtures.Read(SyncPostFixtures.Held));

        var detail = await DetailAsync(id);

        var summary = detail.GetProperty("validationSummary");
        var validation = detail.GetProperty("validation");
        Assert.Equal("held", summary.GetProperty("state").GetString());
        Assert.Equal(1, summary.GetProperty("versionNumber").GetInt32());
        var expected = validation.GetProperty("validation").GetArrayLength() + validation.GetProperty("errors").GetArrayLength();
        Assert.True(expected > 0);
        Assert.Equal(expected, summary.GetProperty("problemCount").GetInt32());
        Assert.Equal(expected, (await RowAsync(id)).GetProperty("validationSummary").GetProperty("problemCount").GetInt32());
    }

    [Fact]
    public async Task Should_ShowSkipCount_When_PostFalseRows()
    {
        var id = await SaveV2Async(V2Fixture("post-false.json"));

        Assert.Equal(1, (await DetailAsync(id)).GetProperty("skipCount").GetInt32());
        Assert.Equal(1, (await RowAsync(id)).GetProperty("skipCount").GetInt32());
    }

    [Fact]
    public async Task Should_ShowOtherUnpostedPushes_When_SameReference()
    {
        var self = await SaveV2Async(V2Fixture("post-false.json"));
        var other = await SaveV2Async(V2Fixture("post-false.json"));
        await SaveV2Async(WithChange(V2Fixture("post-false.json"), n => n["reference"] = "another-reference"));

        var others = (await DetailAsync(self)).GetProperty("otherUnpostedPushes");

        var push = Assert.Single(others.EnumerateArray());
        Assert.Equal(other, push.GetProperty("id").GetInt64());
        Assert.Equal($"SR-{other:000000}", push.GetProperty("publicRef").GetString());
        Assert.Equal("pending", push.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, push.GetProperty("pushId").ValueKind);
        Assert.True(push.TryGetProperty("receivedUtc", out _));
    }

    [Fact]
    public async Task Should_ShowLedgerBatch_When_Unknown()
    {
        var id = await UnknownAsync();
        var attempt = Assert.Single(await Store.AttemptsAsync(id, CancellationToken.None));
        var batch = new ApiBatchStore(BatchesRoot).Load(attempt.PostId!)!;

        var ledger = (await DetailAsync(id)).GetProperty("ledgerBatch");

        Assert.Equal(attempt.PostId, ledger.GetProperty("postId").GetString());
        Assert.Equal(batch.StartedUtc, ledger.GetProperty("createdUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(batch.Counts.Submitted, ledger.GetProperty("counts").GetProperty("submitted").GetInt32());
        Assert.Equal(batch.Counts.Posted, ledger.GetProperty("counts").GetProperty("posted").GetInt32());
        Assert.False(string.IsNullOrEmpty(ledger.GetProperty("status").GetString()));
    }

    /// <summary>Resolved not-posted: back to pending, while the last attempt still names a batch that exists.</summary>
    [Fact]
    public async Task Should_OmitLedgerBatch_When_Pending()
    {
        var id = await UnknownAsync();
        using var admin = _host.User(DashboardRoles.Admin);
        using (var resolved = await admin.PostAsync(
                   $"{V2Route}/{id}/resolve",
                   SavedRequestTestHost.Json(new JsonObject { ["outcome"] = "not-posted", ["note"] = "Not in QuickBooks." }.ToJsonString())))
        {
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        }

        var attempt = Assert.Single(await Store.AttemptsAsync(id, CancellationToken.None));
        Assert.NotNull(new ApiBatchStore(BatchesRoot).Load(attempt.PostId!));

        var detail = await DetailAsync(id);

        Assert.Equal("pending", detail.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("ledgerBatch").ValueKind);
    }

    [Fact]
    public async Task Should_Answer400_When_MinTotalAboveMaxTotal()
    {
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync($"{V2Route}?minTotal=10.00&maxTotal=5.00");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-request", (await SavedRequestTestHost.BodyOf(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Should_Answer400_When_UnknownSort()
    {
        using var viewer = _host.User(DashboardRoles.Viewer);

        using var response = await viewer.GetAsync($"{V2Route}?sort=payee");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-request", (await SavedRequestTestHost.BodyOf(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Should_ApplyV2Filters_When_Listed()
    {
        var match = await SaveV2Async(V2Fixture("with-email.json"));
        await SaveV2Async(WithChange(V2Fixture("with-email.json"), n =>
        {
            n["pushId"] = "9b2f4c1e-0000-4000-8000-000000000001";
            n["controlTotal"] = 500.00m;
        }));

        var page = await GetJsonAsync(
            $"{V2Route}?reference=v2-with-email-2026-08&minTotal=283.42&maxTotal=283.42&emailFrom=BOOKKEEPER&sort=-received");

        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(match, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
    }

    /// <summary>
    /// A pending and a posted request with a malformed pushId (the flag never clears), and a pending duplicate:
    /// each flag is counted among pending requests and among all.
    /// </summary>
    [Fact]
    public async Task Should_CountPendingOnlyFlags_When_StatsRead()
    {
        var posted = await SaveV2Async(V2Fixture("bad-pushid.json"));
        await SaveV2Async(V2Fixture("bad-pushid.json"));
        await PostAsync(posted);
        Assert.Equal(SavedRequestStatus.Posted, (await Store.GetAsync(posted, CancellationToken.None))!.Status);

        var stats = await GetJsonAsync($"{V2Route}/stats");

        Assert.Equal(2, stats.GetProperty("total").GetInt32());
        Assert.Equal(1, stats.GetProperty("byStatus").GetProperty("pending").GetInt32());
        Assert.Equal(1, stats.GetProperty("byStatus").GetProperty("posted").GetInt32());
        Assert.Equal(0, stats.GetProperty("byStatus").GetProperty("discarded").GetInt32());
        AssertFlag(stats.GetProperty("needsAttention"), pending: 1, all: 2);
        AssertFlag(stats.GetProperty("duplicate"), pending: 1, all: 1);
        AssertFlag(stats.GetProperty("hasErrors"), pending: 1, all: 2);

        static void AssertFlag(JsonElement flag, int pending, int all)
        {
            Assert.Equal(pending, flag.GetProperty("pending").GetInt32());
            Assert.Equal(all, flag.GetProperty("all").GetInt32());
        }
    }

    [Fact]
    public async Task Should_CombineStatusWithFlags_When_Listed()
    {
        var posted = await SaveV2Async(V2Fixture("bad-pushid.json"));
        var pending = await SaveV2Async(V2Fixture("bad-pushid.json"));
        await PostAsync(posted);

        foreach (var flag in new[] { "needsAttention", "hasErrors", "duplicate" })
        {
            var page = await GetJsonAsync($"{V2Route}?status=pending&{flag}=true");
            Assert.Equal(pending, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
        }
    }

    [Fact]
    public async Task Should_ListMetadataErrorFirst_When_PushIdMalformed()
    {
        var body = WithChange(V2Fixture("bad-pushid.json"), n =>
        {
            n["transactions"]![0]!["amount"] = "abc";
            n["transactions"]![1]!["post"] = "no";
        });
        var id = await SaveV2Async(body);

        var detail = await DetailAsync(id);

        var errors = detail.GetProperty("readErrors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(3, errors.Count);
        Assert.Equal(SaveMetadataReader.PushIdError, errors[0]);
        Assert.StartsWith("$.transactions[0].amount", errors[1], StringComparison.Ordinal);
        Assert.Equal("$.transactions[1].post: must be true or false", errors[2]);
        Assert.True(detail.GetProperty("needsAttention").GetBoolean());
    }

    [Fact]
    public async Task Should_ListTxnIdPerRow_When_AttemptPosted()
    {
        var id = await SaveV2Async(SyncPostFixtures.Read(SyncPostFixtures.Posted));
        await PostAsync(id);
        var attempt = Assert.Single(await Store.AttemptsAsync(id, CancellationToken.None));
        Assert.Equal(AttemptOutcome.Posted, attempt.Outcome);

        var body = await GetJsonAsync($"{V2Route}/{id}/attempts/{attempt.Id}");

        var posted = body.GetProperty("result").GetProperty("posted").EnumerateArray().ToList();
        Assert.NotEmpty(posted);
        Assert.All(posted, row =>
        {
            Assert.Equal(JsonValueKind.Number, row.GetProperty("index").ValueKind);
            Assert.False(string.IsNullOrEmpty(row.GetProperty("externalId").GetString()));
            Assert.False(string.IsNullOrEmpty(row.GetProperty("txnId").GetString()));
        });
        var resultIds = posted.ToDictionary(r => r.GetProperty("index").GetInt32(), r => r.GetProperty("externalId").GetString());
        var ledgerRows = body.GetProperty("ledgerBatch").GetProperty("posted").EnumerateArray().ToList();
        Assert.Equal(posted.Count, ledgerRows.Count);
        Assert.All(ledgerRows, row =>
            Assert.Equal(resultIds[row.GetProperty("index").GetInt32()], row.GetProperty("externalId").GetString()));
    }

    [Fact]
    public async Task Should_ShowPostSummary_When_Posted()
    {
        var id = await SaveV2Async(SyncPostFixtures.Read(SyncPostFixtures.Posted));
        await PostAsync(id);
        var attempt = Assert.Single(await Store.AttemptsAsync(id, CancellationToken.None));

        var detail = await DetailAsync(id);

        var summary = detail.GetProperty("postSummary");
        Assert.Equal(attempt.PostId, summary.GetProperty("postId").GetString());
        Assert.Equal(2, summary.GetProperty("postedCount").GetInt32());
        Assert.Equal(attempt.StartedBy, summary.GetProperty("postedBy").GetProperty("id").GetString());
        Assert.True(detail.GetProperty("historyCount").GetInt32() > 0);
    }

    /// <summary>Carried from T-1110: a v1 save keeps no email or pushId, so the v2 detail shows none.</summary>
    [Fact]
    public async Task Should_ShowEmailNull_When_SavedThroughV1()
    {
        var (response, saved) = await _host.SaveAsync(V2Fixture("with-email.json"));
        using (response)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var detail = await DetailAsync(saved.GetProperty("id").GetInt64());

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("pushId").ValueKind);
    }
}
