using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1110 (dashboard v2 spec 5, 6.4, 7, 7.1): <c>POST /api/v2/saved-requests</c>. A v2 save reads <c>email</c> and
/// <c>pushId</c> from the body as received, gives rows without an id a <c>row-&lt;n&gt;</c> one, keeps the original bytes,
/// and stores the v2 snapshot beside the v1 one. A v1 save of the same body fills none of the v2 metadata.
/// </summary>
public sealed class V2SaveTests : IDisposable
{
    private const string V2Route = "/api/v2/saved-requests";
    private const string WithEmail = "with-email.json";
    private const string BadPushId = "bad-pushid.json";
    private const string MixedExternalIds = "mixed-externalids.json";
    private const string PostFalse = "post-false.json";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private static string Fixture(string name) => File.ReadAllText(Fixtures.PathOf("saved-requests-v2", name));

    private static byte[] FixtureBytes(string name) => File.ReadAllBytes(Fixtures.PathOf("saved-requests-v2", name));

    private async Task<SavedRequest> StoredAsync(long id) => (await Store.GetAsync(id, CancellationToken.None))!;

    /// <summary>Saves <paramref name="body"/> through v2 as n8n, without an Idempotency-Key.</summary>
    private async Task<HttpResponseMessage> SaveV2Async(HttpContent body)
    {
        using var client = _host.N8n();
        return await client.PostAsync(V2Route, body);
    }

    private Task<HttpResponseMessage> SaveV2Async(string body) => SaveV2Async(SavedRequestTestHost.Json(body));

    private async Task<long> SaveV2CreatedAsync(string body)
    {
        using var response = await SaveV2Async(body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SavedRequestTestHost.BodyOf(response)).GetProperty("id").GetInt64();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    private static string WithChange(string json, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(json)!;
        change(node);
        return node.ToJsonString();
    }

    [Fact]
    public async Task Should_ReturnEmail_When_SavedWithEmail()
    {
        using var response = await SaveV2Async(Fixture(WithEmail));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var email = (await SavedRequestTestHost.BodyOf(response)).GetProperty("email");
        Assert.Equal("August statement for Tropicana", email.GetProperty("subject").GetString());
        Assert.Equal(new DateTime(2026, 8, 6, 9, 15, 0, DateTimeKind.Utc), email.GetProperty("receivedUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("bookkeeper@example.com", email.GetProperty("from").GetString());
        Assert.Equal("<msg-0001@example.com>", email.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task Should_SaveWithoutFlag_When_EmailMalformed()
    {
        var body = WithChange(Fixture(WithEmail), n => n["email"]!["receivedUtc"] = 42);

        using var response = await SaveV2Async(body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await SavedRequestTestHost.BodyOf(response);
        Assert.False(saved.GetProperty("needsAttention").GetBoolean());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("email").GetProperty("receivedUtc").ValueKind);
        Assert.Equal("August statement for Tropicana", saved.GetProperty("email").GetProperty("subject").GetString());
        Assert.Empty((await StoredAsync(saved.GetProperty("id").GetInt64())).Metadata.MetadataErrors);
    }

    [Fact]
    public async Task Should_ReturnPushId_When_Valid()
    {
        using var response = await SaveV2Async(Fixture(WithEmail));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
            (await SavedRequestTestHost.BodyOf(response)).GetProperty("pushId").GetString());
    }

    [Fact]
    public async Task Should_Replay_When_PushIdAndBodyRepeat()
    {
        var first = await SaveV2CreatedAsync(Fixture(WithEmail));

        using var again = await SaveV2Async(Fixture(WithEmail));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("true", Assert.Single(again.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(first, (await SavedRequestTestHost.BodyOf(again)).GetProperty("id").GetInt64());
        Assert.Null(await Store.GetAsync(first + 1, CancellationToken.None));
    }

    [Fact]
    public async Task Should_Answer409PushIdReused_When_BodyDiffers()
    {
        var first = await SaveV2CreatedAsync(Fixture(WithEmail));
        var other = WithChange(Fixture(WithEmail), n => n["reference"] = "v2-with-email-resent");

        using var response = await SaveV2Async(other);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("push-id-reused", problem.GetProperty("type").GetString());
        Assert.Equal(first, problem.GetProperty("savedRequestId").GetInt64());
        Assert.Equal((await StoredAsync(first)).PublicRef, problem.GetProperty("publicRef").GetString());
        Assert.Null(await Store.GetAsync(first + 1, CancellationToken.None));
    }

    [Fact]
    public async Task Should_FlagButStayPostable_When_PushIdMalformed()
    {
        using var saved = await SaveV2Async(Fixture(BadPushId));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var body = await SavedRequestTestHost.BodyOf(saved);
        Assert.True(body.GetProperty("needsAttention").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pushId").ValueKind);
        var id = body.GetProperty("id").GetInt64();
        Assert.Equal([SaveMetadataReader.PushIdError], (await StoredAsync(id)).Metadata.MetadataErrors);

        using var editor = _host.User(DashboardRoles.Editor);
        using var posted = await editor.PostAsync($"{V2Route}/{id}/post", null);

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal(SavedRequestStatus.Posted, (await StoredAsync(id)).Status);
    }

    [Fact]
    public async Task Should_AssignRowIds_When_ExternalIdsMissing()
    {
        var bytes = FixtureBytes(MixedExternalIds);
        using var saved = await SaveV2Async(SavedRequestTestHost.Json(bytes));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var id = (await SavedRequestTestHost.BodyOf(saved)).GetProperty("id").GetInt64();
        using var viewer = _host.User(DashboardRoles.Viewer);

        var version = await GetJsonAsync(viewer, $"{V2Route}/{id}/versions/1");
        using var original = await viewer.GetAsync($"{V2Route}/{id}/original");

        Assert.Equal(
            ["row-2", "row-2-2", "row-3", "row-4", "n8n-77"],
            version.GetProperty("payload").GetProperty("transactions").EnumerateArray()
                .Select(r => r.GetProperty("externalId").GetString()));
        Assert.Equal(bytes, await original.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Should_DiffAddRemoveByExternalId_When_N8nRowsLackedIds()
    {
        var id = await SaveV2CreatedAsync(Fixture(MixedExternalIds));
        using var editor = _host.User(DashboardRoles.Editor);
        var payload = JsonNode.Parse((await GetJsonAsync(editor, $"{V2Route}/{id}/versions/1")).GetProperty("payload").GetRawText())!;
        payload["transactions"]!.AsArray().RemoveAt(1);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{V2Route}/{id}")
        {
            Content = SavedRequestTestHost.Json(new JsonObject { ["payload"] = payload, ["note"] = "drop the Shell row" }.ToJsonString()),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");

        using var edited = await editor.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var change = Assert.Single((await GetJsonAsync(editor, $"{V2Route}/{id}/versions/2")).GetProperty("changes").EnumerateArray());
        Assert.Equal("remove", change.GetProperty("op").GetString());
        Assert.Equal("$.transactions[1]", change.GetProperty("path").GetString());
        Assert.Equal("row-2-2", change.GetProperty("old").GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task Should_SetV2LocationHeader_When_Created()
    {
        using var response = await SaveV2Async(Fixture(WithEmail));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await SavedRequestTestHost.BodyOf(response)).GetProperty("id").GetInt64();
        Assert.Equal($"/api/v2/saved-requests/{id}", response.Headers.Location?.OriginalString);
    }

    /// <summary>
    /// The v2 detail view arrives in T-1111, so this reads the stored record: a v1 save of a body with <c>email</c>
    /// and <c>pushId</c> keeps no metadata, which is what makes the v2 detail show <c>email: null</c>.
    /// </summary>
    [Fact]
    public async Task Should_NotFillMetadata_When_SavedThroughV1()
    {
        var (response, body) = await _host.SaveAsync(Fixture(WithEmail));
        using (response)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var metadata = (await StoredAsync(body.GetProperty("id").GetInt64())).Metadata;
        Assert.False(metadata.HasEmail);
        Assert.Null(metadata.PushId);
        Assert.Empty(metadata.MetadataErrors);
    }

    [Fact]
    public async Task Should_FlagSamePayload_When_OnlyEmailAndPushIdDiffer()
    {
        var first = await SaveV2CreatedAsync(Fixture(WithEmail));
        var resent = WithChange(Fixture(WithEmail), n =>
        {
            n.AsObject().Remove("email");
            n["pushId"] = "9b2f4c1e-0000-4000-8000-000000000001";
        });

        using var response = await SaveV2Async(resent);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var duplicate = (await SavedRequestTestHost.BodyOf(response)).GetProperty("duplicate");
        Assert.Equal(first, duplicate.GetProperty("of").GetInt64());
        Assert.Contains(DuplicateReasons.SamePayload, duplicate.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Should_SkipPostFalseRowOnlyInV2Snapshot_When_Saved()
    {
        var id = await SaveV2CreatedAsync(Fixture(PostFalse));

        var stored = await StoredAsync(id);

        Assert.Equal(0, stored.Validation!.Counts!.Skipped);
        Assert.Equal(1, stored.ValidationV2!.Counts!.Skipped);
    }

    [Fact]
    public async Task Should_MakeOnlyV2SnapshotUnreadable_When_PostIsNotABoolean()
    {
        var body = WithChange(Fixture(PostFalse), n => n["transactions"]![1]!["post"] = "no");

        var stored = await StoredAsync(await SaveV2CreatedAsync(body));

        Assert.Equal(ValidationState.Ok, stored.Validation!.State);
        Assert.Equal(ValidationState.Unreadable, stored.ValidationV2!.State);
        Assert.Equal(["$.transactions[1].post: must be true or false"], stored.ValidationV2.Errors);
    }
}
