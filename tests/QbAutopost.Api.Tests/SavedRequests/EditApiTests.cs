using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1013 (saved-requests spec 6.3, 6.4, 8): <c>PUT /api/v1/saved-requests/{id}</c> and <c>POST /{id}/validate</c>.
/// A full payload replaces the current one as version n+1; the original and every older version stay as they were.
/// </summary>
public sealed class EditApiTests : IDisposable
{
    private const string Route = SavedRequestTestHost.Route;

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private async Task<SavedRequest> StoredAsync(long id) => (await Store.GetAsync(id, CancellationToken.None))!;

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    private static string WithPayee(string payloadJson, string payee)
    {
        var node = JsonNode.Parse(payloadJson)!;
        node["transactions"]![3]!["payee"] = payee;
        return node.ToJsonString();
    }

    [Fact]
    public async Task Should_CreateVersion2WithChanges_When_EditorEditsPayee()
    {
        var id = await _host.SaveBeforeAsync();
        using var editor = _host.User(DashboardRoles.Editor, out var userId);

        using var response = await SavedRequestTestHost.PutAsync(
            editor, id, SavedRequestTestHost.DiffPart("after"), "\"1\"", "fixed payee on row 4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.ToString());
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal(2, body.GetProperty("currentVersion").GetInt32());
        Assert.Equal(2, body.GetProperty("rowVersion").GetInt32());
        Assert.Equal(userId, body.GetProperty("updated").GetProperty("by").GetProperty("id").GetString());
        Assert.Equal("ABC Plumbing LLC", body.GetProperty("payload").GetProperty("transactions")[3].GetProperty("payee").GetString());
        Assert.Equal(["edit", "validate", "post", "discard"], body.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));

        var version = await GetJsonAsync(editor, $"{Route}/{id}/versions/2");
        var change = Assert.Single(version.GetProperty("changes").EnumerateArray());
        Assert.Equal("$.transactions[3].payee", change.GetProperty("path").GetString());
        Assert.Equal("fixed payee on row 4", version.GetProperty("note").GetString());
        var events = await GetJsonAsync(editor, $"{Route}/{id}/events");
        Assert.Equal("edited", events.GetProperty("items")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Should_Return428_When_IfMatchMissing()
    {
        var id = await _host.SaveBeforeAsync();
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PutAsync(editor, id, SavedRequestTestHost.DiffPart("after"), null);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        Assert.Equal("precondition-required", (await SavedRequestTestHost.BodyOf(response)).GetProperty("type").GetString());
        Assert.Equal(1, (await StoredAsync(id)).CurrentVersion);
    }

    [Fact]
    public async Task Should_Return412WithLastEditor_When_IfMatchStale()
    {
        var id = await _host.SaveBeforeAsync();
        using var asha = _host.User(DashboardRoles.Editor, out var ashaId);
        using var ben = _host.User(DashboardRoles.Editor);
        (await SavedRequestTestHost.PutAsync(asha, id, SavedRequestTestHost.DiffPart("after"), "\"1\"")).Dispose();

        using var response = await SavedRequestTestHost.PutAsync(
            ben, id, WithPayee(SavedRequestTestHost.DiffPart("before"), "Someone Else"), "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("edited-by-someone-else", problem.GetProperty("type").GetString());
        Assert.Equal(2, problem.GetProperty("rowVersion").GetInt32());
        var by = problem.GetProperty("updatedBy");
        Assert.Equal(ashaId, by.GetProperty("id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(by.GetProperty("name").GetString()));
        Assert.Equal(2, (await StoredAsync(id)).CurrentVersion);
    }

    [Fact]
    public async Task Should_Return200NoNewVersion_When_OnlyNumberFormattingDiffers()
    {
        var id = await _host.SaveBeforeAsync();
        using var editor = _host.User(DashboardRoles.Editor);
        // Sent as text exactly as written: a JsonNode round trip would tidy the numbers away.
        var formatted = SavedRequestTestHost.DiffPart("before")
            .Replace("\"amount\":100,", "\"amount\":100.00,")
            .Replace("100.25", "100.250")
            .Replace("400.5", "400.50");
        Assert.NotEqual(SavedRequestTestHost.DiffPart("before"), formatted);

        using var response = await SavedRequestTestHost.PutRawAsync(editor, id, "{\"payload\":" + formatted + "}", "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.ToString());
        Assert.Equal(1, (await SavedRequestTestHost.BodyOf(response)).GetProperty("currentVersion").GetInt32());
        Assert.Single(await Store.VersionsAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Should_Return409NotEditable_When_Posting()
    {
        var id = await _host.SaveBeforeAsync();
        var claim = await Store.TryClaimAsync(id, null, "someone", false, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(ClaimKind.Claimed, claim.Kind);
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PutAsync(editor, id, SavedRequestTestHost.DiffPart("after"), "\"2\"");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("not-editable", problem.GetProperty("type").GetString());
        Assert.Contains("posting", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Should_KeepOriginalAndVersion1_When_EditedThreeTimes()
    {
        var original = SavedRequestTestHost.DiffPart("before");
        var id = await _host.SaveBeforeAsync();
        using var editor = _host.User(DashboardRoles.Editor);
        for (var n = 1; n <= 3; n++)
        {
            using var response = await SavedRequestTestHost.PutAsync(editor, id, WithPayee(original, $"Payee {n}"), $"\"{n}\"");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var originalResponse = await editor.GetAsync($"{Route}/{id}/original");
        Assert.Equal(original, await originalResponse.Content.ReadAsStringAsync());
        var versions = await GetJsonAsync(editor, $"{Route}/{id}/versions");
        Assert.Equal([4, 3, 2, 1], versions.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("number").GetInt32()));
        var first = await GetJsonAsync(editor, $"{Route}/{id}/versions/1");
        Assert.Equal("ABC PLUMBING", first.GetProperty("payload").GetProperty("transactions")[3].GetProperty("payee").GetString());
        Assert.Equal(4, (await StoredAsync(id)).CurrentVersion);
    }

    [Fact]
    public async Task Should_StoreUnreadableEdit_When_KindInvalid()
    {
        var id = await _host.SaveBeforeAsync();
        using var editor = _host.User(DashboardRoles.Editor);
        var node = JsonNode.Parse(SavedRequestTestHost.DiffPart("before"))!;
        node["transactions"]![3]!["kind"] = "Bogus";

        using var response = await SavedRequestTestHost.PutAsync(editor, id, node.ToJsonString(), "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.True(body.GetProperty("needsAttention").GetBoolean());
        Assert.Equal("unreadable", body.GetProperty("validation").GetProperty("state").GetString());
        Assert.Equal(2, body.GetProperty("currentVersion").GetInt32());
        Assert.NotEmpty(body.GetProperty("readErrors").EnumerateArray());
        Assert.False((await Store.VersionAsync(id, 2, CancellationToken.None))!.Readable);
    }

    [Theory]
    [InlineData("{\"payload\":[1,2]}")]
    [InlineData("{\"payload\":\"text\"}")]
    [InlineData("{\"note\":\"no payload\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("not json")]
    public async Task Should_Return400_When_EditPayloadNotObject(string body)
    {
        var id = await _host.SaveBeforeAsync();
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PutRawAsync(editor, id, body, "\"1\"");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("not-a-json-object", (await SavedRequestTestHost.BodyOf(response)).GetProperty("type").GetString());
        Assert.Equal(1, (await StoredAsync(id)).CurrentVersion);
    }

    [Fact]
    public async Task Should_NotRecomputeControlTotal_When_AmountEdited()
    {
        var id = await _host.SaveBeforeAsync();
        using var editor = _host.User(DashboardRoles.Editor);
        var node = JsonNode.Parse(SavedRequestTestHost.DiffPart("before"))!;
        node["reference"] = "2026-08-Tropicana";
        node["transactions"]![0]!["amount"] = 150.00m;

        using var response = await SavedRequestTestHost.PutAsync(editor, id, node.ToJsonString(), "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("refused", body.GetProperty("validation").GetProperty("state").GetString());
        Assert.Contains(
            body.GetProperty("validation").GetProperty("errors").EnumerateArray(),
            e => e.GetString()!.StartsWith("control-total-mismatch", StringComparison.Ordinal));
        Assert.Equal(400.50m, (await StoredAsync(id)).ControlTotal);
    }

    [Fact]
    public async Task Should_ClearDuplicateFlag_When_EditMakesItUnique()
    {
        await _host.SaveFixtureAsync("n8n-good.json");
        var second = await _host.SaveFixtureAsync("n8n-good.json");
        Assert.NotEmpty((await StoredAsync(second)).DuplicateReasons);
        using var editor = _host.User(DashboardRoles.Editor);
        var node = JsonNode.Parse(SavedRequestTestHost.Fixture("n8n-good.json"))!;
        node["reference"] = "a different reference";
        node["controlTotal"] = 285.42m;
        var rows = node["transactions"]!.AsArray();
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i]!["externalId"] = $"other-{i}";
            rows[i]!["amount"] = (decimal)rows[i]!["amount"]! + 1m;
            rows[i]!["memo"] = $"UNRELATED {i}";
            rows[i]!["payee"] = $"UNRELATED {i}";
        }

        using var response = await SavedRequestTestHost.PutAsync(editor, second, node.ToJsonString(), "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.False(body.GetProperty("duplicate").GetProperty("flagged").GetBoolean());
        Assert.Empty((await StoredAsync(second)).DuplicateReasons);
    }

    [Fact]
    public async Task Should_RefreshSnapshot_When_Revalidate()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        var before = (await StoredAsync(id)).Validation!.ValidatedUtc;
        await Task.Delay(30);
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "validate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.ToString());
        var body = await SavedRequestTestHost.BodyOf(response);
        Assert.True(body.GetProperty("validation").GetProperty("validatedUtc").GetDateTime() > before);
        Assert.Equal(1, body.GetProperty("currentVersion").GetInt32());
        var events = await GetJsonAsync(editor, $"{Route}/{id}/events");
        Assert.Equal("validated", events.GetProperty("items")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Should_Return409NotEditable_When_ValidatingNonPending()
    {
        var id = await _host.SaveFixtureAsync("n8n-good.json");
        await Store.TryClaimAsync(id, null, "someone", false, DateTime.UtcNow, CancellationToken.None);
        using var editor = _host.User(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "validate");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("not-editable", problem.GetProperty("type").GetString());
        Assert.Contains("posting", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Should_Return404_When_EditingOrValidatingUnknownId()
    {
        using var editor = _host.User(DashboardRoles.Editor);

        using var put = await SavedRequestTestHost.PutAsync(editor, 999, SavedRequestTestHost.DiffPart("after"), "\"1\"");
        using var validate = await SavedRequestTestHost.PostActionAsync(editor, 999, "validate");

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, validate.StatusCode);
    }
}
