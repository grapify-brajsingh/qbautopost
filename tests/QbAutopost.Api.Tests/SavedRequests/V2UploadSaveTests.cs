using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1213 (dashboard-api-m12 7.2-7.6): <c>POST /api/v2/saved-requests</c> with <c>upload</c>. Version 1 is what the
/// model read (with the body's reference and control total), version 2 is the reviewed body when its rows differ, and
/// the upload columns come from the extraction record, never from the body.
/// </summary>
public sealed class V2UploadSaveTests : IDisposable
{
    private const string V2Route = "/api/v2/saved-requests";
    private const string Feed = "/api/v2/audit-events";
    private const string FileName = "Chase-August-2026.pdf";
    private const string Sha = "5d41402abc4b2a76b9719d911017c592aa7f3fd6c1b8e0b1fa1b0a2c3d4e5f60";
    private const string Unedited = "upload-unedited.json";
    private const string Reviewed = "upload-reviewed.json";

    private readonly SavedRequestTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private FakeSavedRequestStore Store => _host.Factory.Store;

    private static string Fixture(string name) => File.ReadAllText(Fixtures.PathOf("saved-requests-v2", name));

    /// <summary>An extraction record of <paramref name="userId"/>, read <paramref name="extractedAgo"/> ago (default 1 h), kept 24 h.</summary>
    private async Task<Guid> SeedExtractionAsync(string userId, TimeSpan? extractedAgo = null)
    {
        var extracted = DateTime.UtcNow - (extractedAgo ?? TimeSpan.FromHours(1));
        var extraction = new StatementExtraction(
            Guid.NewGuid(), userId, FileName, 48213, Sha, 2, "~google/gemini-pro-latest", "2026-10-28.api-1",
            JsonNode.Parse(Fixture("upload-extraction.json"))!.AsObject(), extracted, extracted.AddHours(24), null);
        await Store.CreateExtractionAsync(extraction, default);
        return extraction.Id;
    }

    /// <summary>The fixture body with <c>upload: { extractionId }</c> and any further change.</summary>
    private static string Body(string fixture, Guid extractionId, Action<JsonObject>? change = null)
    {
        var node = JsonNode.Parse(Fixture(fixture))!.AsObject();
        node["upload"] = new JsonObject { ["extractionId"] = extractionId.ToString() };
        change?.Invoke(node);
        return node.ToJsonString();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, V2Route) { Content = SavedRequestTestHost.Json(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request);
    }

    private static async Task<long> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SavedRequestTestHost.BodyOf(response)).GetProperty("id").GetInt64();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SavedRequestTestHost.BodyOf(response);
    }

    private async Task<IReadOnlyList<SavedRequestVersion>> VersionsAsync(long id) => await Store.VersionsAsync(id, default);

    private static string Problem(JsonElement body) => body.GetProperty("type").GetString()!;

    [Fact]
    public async Task Should_CreateVersion1And2_When_TwoCellsEditedAndRowAdded()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);

        using var response = await PostAsync(client, Body(Reviewed, extractionId));

        var id = await CreatedIdAsync(response);
        var versions = await VersionsAsync(id);
        Assert.Equal(2, versions.Count);
        var v1 = versions[0];
        Assert.Equal($"Read from {FileName}", v1.Note);
        Assert.Equal("Upload Chase 4521 2026-08", v1.Payload.GetProperty("reference").GetString());
        Assert.Equal(JsonValueKind.Null, v1.Payload.GetProperty("controlTotal").ValueKind);
        var extracted = JsonNode.Parse(Fixture("upload-extraction.json"))!["transactions"]!.ToJsonString();
        Assert.Equal(
            JsonNode.Parse(extracted)!.ToJsonString(),
            JsonNode.Parse(v1.Payload.GetProperty("transactions").GetRawText())!.ToJsonString());
        Assert.Empty(v1.Changes);
        var v2 = versions[1];
        Assert.Equal("Reviewed before saving", v2.Note);
        Assert.Equal(v1.CreatedBy, v2.CreatedBy);
        Assert.Equal(v1.CreatedUtc, v2.CreatedUtc);
        Assert.Equal(3, v2.Changes.Count);
        Assert.Equal(
            new[] { "replace $.transactions[0].amount", "replace $.transactions[1].payee", "add $.transactions[3]" },
            v2.Changes.Select(c => $"{c.Op} {c.Path}"));
        Assert.Equal(2, (await Store.GetAsync(id, default))!.CurrentVersion);
    }

    [Fact]
    public async Task Should_CreateOnlyVersion1_When_NoRowEdits()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);

        using var response = await PostAsync(client, Body(Unedited, extractionId));

        var id = await CreatedIdAsync(response);
        var version = Assert.Single(await VersionsAsync(id));
        Assert.Equal($"Read from {FileName}", version.Note);
        Assert.Equal(1, (await Store.GetAsync(id, default))!.CurrentVersion);
    }

    [Fact]
    public async Task Should_ShowReceivedAndRowEvents_When_UploadSaved()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);
        using var response = await PostAsync(client, Body(Reviewed, extractionId));
        var id = await CreatedIdAsync(response);

        var feed = await GetJsonAsync(client, $"{Feed}?requestId={id}");

        var items = feed.GetProperty("items").EnumerateArray().ToList();
        var received = Assert.Single(items, e => e.GetProperty("action").GetString() == "received");
        Assert.Equal("user", received.GetProperty("actor").GetProperty("kind").GetString());
        Assert.Equal(userId, received.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal("upload", received.GetProperty("detail").GetProperty("source").GetString());
        Assert.Equal(FileName, received.GetProperty("detail").GetProperty("fileName").GetString());
        var rows = items.Where(e => e.GetProperty("action").GetString()!.StartsWith("row-", StringComparison.Ordinal))
            .Select(e => e.GetProperty("action").GetString()).OrderBy(a => a, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "row-added", "row-edited", "row-edited" }, rows);
    }

    [Fact]
    public async Task Should_Return409WithFirstId_When_SavedAgain()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);
        using var first = await PostAsync(client, Body(Unedited, extractionId));
        var id = await CreatedIdAsync(first);

        using var again = await PostAsync(client, Body(Reviewed, extractionId), key: "second-try");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await SavedRequestTestHost.BodyOf(again);
        Assert.Equal("extraction-already-saved", Problem(problem));
        Assert.Equal(id, problem.GetProperty("savedRequestId").GetInt64());
        Assert.Equal($"SR-{id:000000}", problem.GetProperty("publicRef").GetString());
        Assert.Null(await Store.GetAsync(id + 1, default));
        Assert.Single(await VersionsAsync(id));
    }

    [Fact]
    public async Task Should_ReplayCreated_When_SameKeyAndBodyRetried()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);
        var body = Body(Reviewed, extractionId);
        using var first = await PostAsync(client, body, key: "upload-key-1");
        var id = await CreatedIdAsync(first);

        using var again = await PostAsync(client, body, key: "upload-key-1");

        // As an ordinary v2 replay (V2SaveTests): the stored answer with Idempotency-Replayed.
        Assert.True(again.IsSuccessStatusCode);
        Assert.Equal("true", Assert.Single(again.Headers.GetValues("Idempotency-Replayed")));
        Assert.Equal(id, (await SavedRequestTestHost.BodyOf(again)).GetProperty("id").GetInt64());
        Assert.Null(await Store.GetAsync(id + 1, default));
    }

    [Fact]
    public async Task Should_Return410_When_Expired()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId, extractedAgo: TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

        using var response = await PostAsync(client, Body(Unedited, extractionId));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var problem = await SavedRequestTestHost.BodyOf(response);
        Assert.Equal("extraction-expired", Problem(problem));
        Assert.Contains("Upload the PDF again.", problem.GetProperty("detail").GetString());
        Assert.Null(await Store.GetExtractionAsync(extractionId, default));
        Assert.Equal(0, (await Store.StatsAsync(default)).Total);
    }

    [Fact]
    public async Task Should_Return404_When_OtherUsersExtraction()
    {
        _host.User(DashboardRoles.Editor, out var owner).Dispose();
        var extractionId = await SeedExtractionAsync(owner);
        using var other = _host.User(DashboardRoles.Editor);

        using var response = await PostAsync(other, Body(Unedited, extractionId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not-found", Problem(await SavedRequestTestHost.BodyOf(response)));
        Assert.Null((await Store.GetExtractionAsync(extractionId, default))!.SavedRequestId);
        Assert.Equal(0, (await Store.StatsAsync(default)).Total);
    }

    [Fact]
    public async Task Should_Return403_When_ApiKeyClientSendsUpload()
    {
        var extractionId = await SeedExtractionAsync("someone");
        using var n8n = _host.N8n();

        using var response = await PostAsync(n8n, Body(Unedited, extractionId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, (await Store.StatsAsync(default)).Total);
    }

    [Fact]
    public async Task Should_Return400_When_UploadWithPushId()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);

        using var response = await PostAsync(
            client, Body(Unedited, extractionId, n => n["pushId"] = "3f2504e0-4f89-11d3-9a0c-0305e82c3301"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-request", Problem(await SavedRequestTestHost.BodyOf(response)));
        Assert.Equal(0, (await Store.StatsAsync(default)).Total);
    }

    [Fact]
    public async Task Should_Return400_When_UploadWithEmail()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);

        using var response = await PostAsync(
            client, Body(Unedited, extractionId, n => n["email"] = new JsonObject { ["subject"] = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-request", Problem(await SavedRequestTestHost.BodyOf(response)));
    }

    [Fact]
    public async Task Should_Return400_When_ExtractionIdNotUuid()
    {
        using var client = _host.User(DashboardRoles.Editor);

        using var response = await PostAsync(
            client, Body(Unedited, Guid.Empty, n => n["upload"] = new JsonObject { ["extractionId"] = "not-a-uuid" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-request", Problem(await SavedRequestTestHost.BodyOf(response)));
    }

    [Fact]
    public async Task Should_IgnoreClientUploadFields_When_BodyHasFileName()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);
        var body = Body(Unedited, extractionId, n =>
        {
            var upload = n["upload"]!.AsObject();
            upload["fileName"] = "evil.pdf";
            upload["sha256"] = new string('0', 64);
            upload["sizeBytes"] = 1;
        });

        using var response = await PostAsync(client, body);

        var id = await CreatedIdAsync(response);
        var upload = (await Store.GetAsync(id, default))!.Upload!;
        Assert.Equal(FileName, upload.FileName);
        Assert.Equal(Sha, upload.Sha256);
        Assert.Equal(48213, upload.SizeBytes);
    }

    [Fact]
    public async Task Should_ReturnUploadOnDetailAndList_When_Saved()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);
        using var response = await PostAsync(client, Body(Unedited, extractionId));
        var id = await CreatedIdAsync(response);

        var detail = await GetJsonAsync(client, $"{V2Route}/{id}");
        var row = Assert.Single((await GetJsonAsync(client, V2Route)).GetProperty("items").EnumerateArray());

        foreach (var view in new[] { detail, row })
        {
            var upload = view.GetProperty("upload");
            Assert.Equal(FileName, upload.GetProperty("fileName").GetString());
            Assert.Equal(48213, upload.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(Sha, upload.GetProperty("sha256").GetString());
            Assert.Equal("~google/gemini-pro-latest", upload.GetProperty("model").GetString());
            Assert.Equal("2026-10-28.api-1", upload.GetProperty("promptVersion").GetString());
            Assert.Equal(JsonValueKind.String, upload.GetProperty("extractedUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, view.GetProperty("email").ValueKind);
        }
    }

    [Fact]
    public async Task Should_ReturnNullUpload_When_PlainV2Save()
    {
        using var client = _host.User(DashboardRoles.Editor);
        using var response = await PostAsync(client, Fixture(Unedited));
        var id = await CreatedIdAsync(response);

        var detail = await GetJsonAsync(client, $"{V2Route}/{id}");

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("upload").ValueKind);
    }

    [Fact]
    public async Task Should_FindByFileName_When_QMatches()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);
        using var saved = await PostAsync(client, Body(Unedited, extractionId));
        var id = await CreatedIdAsync(saved);
        using var plain = await PostAsync(client, Fixture(Reviewed));
        await CreatedIdAsync(plain);

        var list = await GetJsonAsync(client, $"{V2Route}?q=chase-august");

        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Should_ExcludeUploadFromHashAndChanges_When_Saved()
    {
        using var client = _host.User(DashboardRoles.Editor, out var userId);
        var extractionId = await SeedExtractionAsync(userId);

        using var response = await PostAsync(client, Body(Reviewed, extractionId));

        var id = await CreatedIdAsync(response);
        var stored = (await Store.GetAsync(id, default))!;
        var withoutUpload = ExternalIdAssigner.AssignOnSave(JsonDocument.Parse(Fixture(Reviewed)).RootElement);
        Assert.Equal(PayloadCanonicalizer.Sha256(withoutUpload, PayloadRules.V2), stored.CanonicalSha256);
        Assert.DoesNotContain(
            (await VersionsAsync(id)).SelectMany(v => v.Changes),
            c => c.Path.Contains("upload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_SaveAsToday_When_NoUpload()
    {
        using var client = _host.User(DashboardRoles.Editor);

        using var response = await PostAsync(client, Fixture(Reviewed));

        var id = await CreatedIdAsync(response);
        var stored = (await Store.GetAsync(id, default))!;
        var version = Assert.Single(await VersionsAsync(id));
        Assert.Null(version.Note);
        Assert.Null(stored.Upload);
        Assert.Equal(Fixture(Reviewed), stored.OriginalBody);
        var created = Assert.Single(await Store.EventsAsync(id, default), e => e.Kind == SavedRequestEvents.Created);
        Assert.Null(created.Detail);
    }

    [Fact]
    public async Task Should_WriteEmailSource_When_N8nSavesWithEmail()
    {
        using var n8n = _host.N8n();
        using var response = await PostAsync(n8n, Fixture("with-email.json"));
        var id = await CreatedIdAsync(response);

        var created = Assert.Single(await Store.EventsAsync(id, default), e => e.Kind == SavedRequestEvents.Created);

        Assert.Equal("email", created.Detail?["source"]?.GetValue<string>());
    }
}
