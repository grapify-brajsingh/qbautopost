using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1112 (dashboard v2 spec 3, 5.1, 5.3, 6.4, 7, 7.1, 10 item 4.5): <c>PUT /api/v2/saved-requests/{id}</c> and the v2
/// <c>validate</c>, <c>discard</c>, <c>restore</c> and <c>resolve</c>. Edits are hashed and diffed by the v2 rules,
/// new rows get <c>edit&lt;v&gt;-&lt;n&gt;</c> ids, the snapshot honours <c>post: false</c>, every reply is the v2
/// detail and every 412 carries <c>updatedUtc</c>. The v1 routes keep their behaviour (their own tests are unedited).
/// </summary>
public sealed class V2EditTests : IDisposable
{
    private const string V2Route = "/api/v2/saved-requests";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private async Task<SavedRequest> StoredAsync(long id) => (await Store.GetAsync(id, CancellationToken.None))!;

    private static string V2Fixture(string name) => File.ReadAllText(Fixtures.PathOf("saved-requests-v2", name));

    private static string ThreeRows() => V2Fixture("three-rows-with-email.json");

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

    private static async Task<HttpResponseMessage> PutV2Async(HttpClient client, long id, string payloadJson, string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{V2Route}/{id}")
        {
            Content = SavedRequestTestHost.Json(new JsonObject { ["payload"] = JsonNode.Parse(payloadJson) }.ToJsonString()),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ActionV2Async(
        HttpClient client, long id, string action, string? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{V2Route}/{id}/{action}");
        if (body is not null)
        {
            request.Content = SavedRequestTestHost.Json(body);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    private const string DiscardBody = """{ "reason": "sent twice" }""";

    private const string ResolveBody = """{ "outcome": "not-posted", "note": "Not in QuickBooks." }""";

    /// <summary>Runs <paramref name="action"/> through v2 as an admin, with <paramref name="ifMatch"/> where the route takes one.</summary>
    private async Task<HttpResponseMessage> ChangeV2Async(HttpClient admin, long id, string action, string ifMatch) =>
        action switch
        {
            "edit" => await PutV2Async(
                admin, id, WithChange(ThreeRows(), n => n["transactions"]![0]!["memo"] = "edited memo"), ifMatch),
            "validate" => await ActionV2Async(admin, id, "validate"),
            "discard" => await ActionV2Async(admin, id, "discard", DiscardBody, ifMatch),
            "restore" => await ActionV2Async(admin, id, "restore", null, ifMatch),
            "resolve" => await ActionV2Async(admin, id, "resolve", ResolveBody, ifMatch),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

    /// <summary>Puts the request where <paramref name="action"/> applies: discarded for restore, unknown for resolve.</summary>
    private void Prepare(long id, string action)
    {
        switch (action)
        {
            case "restore":
                Store.Change(id, r => r with { Status = SavedRequestStatus.Discarded });
                break;
            case "resolve":
                Store.Change(id, r => r with { Status = SavedRequestStatus.Unknown });
                break;
        }
    }

    // ---- edit ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_RecordPostOffChange_When_RowTurnedOff()
    {
        var id = await SaveV2Async(ThreeRows());
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await PutV2Async(
            editor, id, WithChange(ThreeRows(), n => n["transactions"]![2]!["post"] = false), "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var version = await GetJsonAsync(editor, $"{V2Route}/{id}/versions/2");
        var change = Assert.Single(version.GetProperty("changes").EnumerateArray());
        Assert.Equal("replace", change.GetProperty("op").GetString());
        Assert.Equal("$.transactions[2].post", change.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.True, change.GetProperty("old").ValueKind);
        Assert.Equal(JsonValueKind.False, change.GetProperty("new").ValueKind);
    }

    [Fact]
    public async Task Should_Return200Unchanged_When_OnlyEmailChanged()
    {
        var id = await SaveV2Async(ThreeRows());
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await PutV2Async(
            editor, id, WithChange(ThreeRows(), n => n["email"]!["subject"] = "A different subject"), "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.Equal(1, (await SavedRequestTestHost.BodyOf(response)).GetProperty("currentVersion").GetInt32());
        Assert.Equal(1, (await StoredAsync(id)).CurrentVersion);
    }

    [Fact]
    public async Task Should_KeepEmailMetadata_When_EditDropsEmail()
    {
        var id = await SaveV2Async(ThreeRows());
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await PutV2Async(
            editor, id,
            WithChange(ThreeRows(), n =>
            {
                n.AsObject().Remove("email");
                n["transactions"]![0]!["memo"] = "edited memo";
            }),
            "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal(2, body.GetProperty("currentVersion").GetInt32());
        Assert.Equal("August statement for Tropicana", body.GetProperty("email").GetProperty("subject").GetString());
        Assert.Equal("<msg-0003@example.com>", (await StoredAsync(id)).Metadata.EmailMessageId);
    }

    [Fact]
    public async Task Should_AssignEditId_When_NewRowLacksId()
    {
        var id = await SaveV2Async(ThreeRows());
        using var editor = _host.User(DashboardRoles.Editor);
        var edited = WithChange(ThreeRows(), n =>
        {
            var row = n["transactions"]![1]!.DeepClone().AsObject();
            row.Remove("externalId");
            n["transactions"]!.AsArray().Add(row);
        });

        using var response = await PutV2Async(editor, id, edited, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await SavedRequestTestHost.BodyOf(response)).GetProperty("payload").GetProperty("transactions");
        Assert.Equal("edit2-4", rows[3].GetProperty("externalId").GetString());
        Assert.Equal("row-1", rows[0].GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task Should_SetNeedsAttentionV2_When_PostValueInvalid()
    {
        var id = await SaveV2Async(ThreeRows());
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await PutV2Async(
            editor, id, WithChange(ThreeRows(), n => n["transactions"]![1]!["post"] = "no"), "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.True(body.GetProperty("needsAttention").GetBoolean());
        Assert.Equal("unreadable", body.GetProperty("validationSummary").GetProperty("state").GetString());
        Assert.Contains(
            "$.transactions[1].post: must be true or false",
            body.GetProperty("readErrors").EnumerateArray().Select(e => e.GetString()));
        var stored = await StoredAsync(id);
        Assert.True(stored.NeedsAttentionV2);
        Assert.False(stored.NeedsAttention);
    }

    // ---- validate ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_SnapshotHonoursPostFalse_When_Revalidated()
    {
        var id = await SaveV2Async(V2Fixture("held-row-off.json"));
        Store.Change(id, r => r with { ValidationV2 = null });
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await ActionV2Async(editor, id, "validate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var v2 = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("ok", v2.GetProperty("validationSummary").GetProperty("state").GetString());
        var v1 = await GetJsonAsync(editor, $"{SavedRequestTestHost.Route}/{id}");
        Assert.Equal("held", v1.GetProperty("validation").GetProperty("state").GetString());
    }

    // ---- every change ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("edit")]
    [InlineData("validate")]
    [InlineData("discard")]
    [InlineData("restore")]
    [InlineData("resolve")]
    public async Task Should_IncludeUpdatedUtc_When_Stale(string action)
    {
        var id = await SaveV2Async(ThreeRows());
        Prepare(id, action);
        var changedUtc = new DateTime(2026, 10, 28, 7, 45, 12, DateTimeKind.Utc);
        using var admin = _host.User(DashboardRoles.Admin, out var adminId);
        var someoneElse = () => Store.Change(id, r => r with
        {
            CurrentVersion = action == "validate" ? r.CurrentVersion + 1 : r.CurrentVersion,
            UpdatedUtc = changedUtc,
            UpdatedBy = adminId,
        });
        if (action == "validate")
        {
            // validate takes no If-Match: the change lands between its read and its write.
            Store.BeforeNextSetValidation = someoneElse;
        }
        else
        {
            someoneElse();
        }

        using var response = await ChangeV2Async(admin, id, action, "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = await SavedRequestTestHost.BodyOf(response);
        var stored = await StoredAsync(id);
        Assert.Equal("edited-by-someone-else", problem.GetProperty("type").GetString());
        Assert.Equal(changedUtc, problem.GetProperty("updatedUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(stored.RowVersion, problem.GetProperty("rowVersion").GetInt32());
        Assert.Equal(adminId, problem.GetProperty("updatedBy").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("validate")]
    [InlineData("discard")]
    [InlineData("restore")]
    [InlineData("resolve")]
    public async Task Should_ReplyWithV2Detail_When_ChangeSucceeds(string action)
    {
        var id = await SaveV2Async(ThreeRows());
        Prepare(id, action);
        using var admin = _host.User(DashboardRoles.Admin);
        var ifMatch = $"\"{(await StoredAsync(id)).RowVersion}\"";

        using var response = await ChangeV2Async(admin, id, action, ifMatch);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await SavedRequestTestHost.BodyOf(response);
        using var detail = await admin.GetAsync($"{V2Route}/{id}");
        Assert.Equal(detail.Headers.ETag?.Tag, response.Headers.ETag?.Tag);
        Assert.Equal((await SavedRequestTestHost.BodyOf(detail)).GetRawText(), reply.GetRawText());
        Assert.True(reply.TryGetProperty("validationSummary", out _));
    }

    [Fact]
    public async Task Should_OmitUpdatedUtc_When_StaleThroughV1()
    {
        var id = await SaveV2Async(ThreeRows());
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "discard", DiscardBody, "\"7\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = await SavedRequestTestHost.BodyOf(response);
        Assert.True(problem.TryGetProperty("rowVersion", out _));
        Assert.False(problem.TryGetProperty("updatedUtc", out _));
    }
}
