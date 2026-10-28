using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1210 (dashboard-api-m12 6.7, 7.1-7.5): saving a reviewed upload in one transaction, and the upload duplicate
/// candidates, pinned on every store.
/// </summary>
public abstract class UploadSaveStoreContract
{
    private const string User = "user-7";
    private const string Actor = "user-7";
    private const string Sha = "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private const string ExtractedRows =
        """[{"externalId":"row-1","kind":"deposit","date":"2026-03-01","amount":12.50,"account":"Checking 1234","payee":"Acme","memo":"x"},{"externalId":"row-2","kind":"check","date":"2026-03-02","amount":40.00,"account":"Checking 1234","payee":"Bolt","memo":"y"}]""";

    private const string EditedRows =
        """[{"externalId":"row-1","kind":"deposit","date":"2026-03-01","amount":13.50,"account":"Checking 1234","payee":"Acme","memo":"x"},{"externalId":"row-2","kind":"check","date":"2026-03-02","amount":40.00,"account":"Checking 1234","payee":"Bolt","memo":"y"},{"externalId":"dash-1","kind":"check","date":"2026-03-03","amount":5.00,"account":"Checking 1234","payee":"Cork","memo":"z"}]""";

    private const string Changes =
        """[{"op":"replace","path":"$.transactions[0].amount","old":12.50,"new":13.50},{"op":"add","path":"$.transactions[2]","old":null,"new":{"externalId":"dash-1"}}]""";

    /// <summary>One object that is every store, as in the application.</summary>
    protected abstract IUploadStore CreateStore();

    private static StatementExtraction Extraction(
        string fileName = "Bank-March.pdf", string sha = Sha, DateTime? expires = null, string user = User) =>
        new(Guid.NewGuid(), user, fileName, 12345, sha, 3, "vendor/model-x", "2026-10-28.api-1",
            JsonNode.Parse($$"""{"isBankStatement":true,"reference":"MAR","controlTotal":null,"transactions":{{ExtractedRows}}}""")!.AsObject(),
            Now.AddHours(-1), expires ?? Now.AddHours(23), null);

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static NewUploadSave Save(
        StatementExtraction extraction, bool edited = false, string? key = null, string user = User, DateTime? now = null,
        string reference = "R-MARCH")
    {
        var at = now ?? Now;
        var version1 = Element($$"""{"reference":"{{reference}}","controlTotal":null,"transactions":{{ExtractedRows}}}""");
        var version2 = edited ? Element($$"""{"reference":"{{reference}}","controlTotal":null,"transactions":{{EditedRows}}}""") : (JsonElement?)null;
        var current = version2 ?? version1;
        var body = $$"""{"reference":"{{reference}}","controlTotal":null,"upload":{"extractionId":"{{extraction.Id}}","fileName":"evil.pdf"},"transactions":{{(edited ? EditedRows : ExtractedRows)}}}""";
        var request = new NewSavedRequest(
            Actor, Actor, body, current, SavedPayloadReader.Read(current), PayloadCanonicalizer.Sha256(current),
            new HashSet<string> { "fp-" + reference }, key,
            new ValidationSnapshot(ValidationState.Ok, at, edited ? 2 : 1, null, [], [], null),
            new DuplicateMatch(null, []), at);
        return new NewUploadSave(
            request, extraction.Id, user, version1, version2, edited ? JsonNode.Parse(Changes)!.AsArray() : null,
            $"Read from {extraction.FileName}", "Reviewed before saving");
    }

    private static async Task<StatementExtraction> StoredAsync(IUploadStore store, StatementExtraction? extraction = null)
    {
        var e = extraction ?? Extraction();
        await store.CreateExtractionAsync(e, default);
        return e;
    }

    private static async Task<long> PlainSaveAsync(IUploadStore store, DateTime now, string reference = "PLAIN")
    {
        var body = $$"""{"reference":"{{reference}}","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
        var payload = Element(body);
        var request = new NewSavedRequest(
            "api-client:n8n", "api-client:n8n", body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string> { "fp-plain" }, null, new ValidationSnapshot(ValidationState.Ok, now, 1, null, [], [], null),
            new DuplicateMatch(null, []), now);
        return (await ((ISavedRequestStore)store).CreateAsync(request, default)).Record.Id;
    }

    private static async Task<int> RequestCountAsync(IUploadStore store) =>
        (await ((ISavedRequestStore)store).ListAsync(
            new SavedRequestQuery([], null, null, null, null, null, null, "-created", 1, 100), default)).Total;

    private static Task<int> AuditCountAsync(IUploadStore store) =>
        ((ISavedRequestAuditStore)store).AuditEventCountAsync(
            new AuditQuery(null, null, null, null, null, null, [], null, IncludeUserEvents: false, 1, 1), default);

    private static bool SameJson(JsonElement a, JsonElement b) =>
        JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), JsonNode.Parse(b.GetRawText()));

    [ContractFact]
    public async Task Should_WriteVersion1Only_When_NoRowChanges()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store);
        var save = Save(extraction);

        var result = await store.SaveUploadAsync(save, default);

        Assert.Equal(UploadSaveKind.Created, result.Kind);
        var versions = await ((ISavedRequestStore)store).VersionsAsync(result.Record!.Id, default);
        var v1 = Assert.Single(versions);
        Assert.Equal(1, v1.Number);
        Assert.True(SameJson(save.Version1Payload, v1.Payload));
        Assert.Empty(v1.Changes);
        Assert.Equal("Read from Bank-March.pdf", v1.Note);
        Assert.Equal(Actor, v1.CreatedBy);
        Assert.Equal(Now, v1.CreatedUtc);
        Assert.Equal(1, result.Record.CurrentVersion);
    }

    [ContractFact]
    public async Task Should_WriteVersion2WithChanges_When_RowsEdited()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store);
        var save = Save(extraction, edited: true);

        var result = await store.SaveUploadAsync(save, default);

        Assert.Equal(UploadSaveKind.Created, result.Kind);
        var record = result.Record!;
        var versions = await ((ISavedRequestStore)store).VersionsAsync(record.Id, default);
        Assert.Equal([1, 2], versions.Select(v => v.Number));
        var (v1, v2) = (versions[0], versions[1]);
        Assert.True(SameJson(save.Version1Payload, v1.Payload));
        Assert.Empty(v1.Changes);
        Assert.Equal("Read from Bank-March.pdf", v1.Note);
        Assert.True(SameJson(save.Version2Payload!.Value, v2.Payload));
        Assert.Equal("Reviewed before saving", v2.Note);
        Assert.Equal(["replace", "add"], v2.Changes.Select(c => c.Op));
        Assert.Equal("$.transactions[0].amount", v2.Changes[0].Path);
        Assert.Equal((v1.CreatedUtc, v1.CreatedBy), (v2.CreatedUtc, v2.CreatedBy));
        Assert.Equal((Now, Actor), (v2.CreatedUtc, v2.CreatedBy));
        Assert.Equal(2, record.CurrentVersion);
        Assert.Equal(3, record.TransactionCount);
        Assert.True(SameJson(save.Version2Payload.Value, record.CurrentPayload));
    }

    [ContractFact]
    public async Task Should_CopyUploadColumnsFromRecord_When_Saved()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store);

        var result = await store.SaveUploadAsync(Save(extraction), default);

        var expected = new UploadMeta("Bank-March.pdf", 12345, Sha, "vendor/model-x", "2026-10-28.api-1", Now.AddHours(-1));
        Assert.Equal(expected, result.Record!.Upload);
        Assert.Equal(expected, (await ((ISavedRequestStore)store).GetAsync(result.Record.Id, default))!.Upload);
        Assert.Equal(expected, (await ((ISavedRequestV2Store)store).GetV2Async(result.Record.Id, default))!.Upload);
        Assert.Equal(result.Record.Id, (await store.GetExtractionAsync(extraction.Id, default))!.SavedRequestId);
        Assert.False(result.Record.Metadata.HasEmail);
    }

    [ContractFact]
    public async Task Should_WriteCreatedDetail_When_UploadSaved()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store);

        var result = await store.SaveUploadAsync(Save(extraction, edited: true), default);

        var events = await ((ISavedRequestStore)store).EventsAsync(result.Record!.Id, default);
        var created = Assert.Single(events, e => e.Kind == SavedRequestEvents.Created);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["source"] = "upload", ["fileName"] = "Bank-March.pdf" }, created.Detail));
        Assert.Equal((Now, Actor), (created.AtUtc, created.Actor));

        var feed = await ((ISavedRequestAuditStore)store).AuditEventsAsync(
            new AuditQuery(null, null, null, result.Record.Id, null, null, [], null, IncludeUserEvents: false, 1, 50), default);
        var received = Assert.Single(feed.Items, e => e.Action == AuditActions.Received);
        Assert.Equal("upload", received.Detail?["source"]?.GetValue<string>());
        Assert.Equal("Bank-March.pdf", received.Detail?["fileName"]?.GetValue<string>());
        Assert.Equal(2, feed.Items.Count(e => e.VersionNumber == 2));
    }

    [ContractFact]
    public async Task Should_ReturnAlreadySaved_When_SavedTwice()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store);
        var first = await store.SaveUploadAsync(Save(extraction), default);

        var second = await store.SaveUploadAsync(Save(extraction, edited: true, key: "other-key"), default);

        Assert.Equal(UploadSaveKind.AlreadySaved, second.Kind);
        Assert.Equal(first.Record!.Id, second.SavedRequestId);
        Assert.Equal(first.Record.PublicRef, second.PublicRef);
        Assert.Null(second.Record);
    }

    [ContractFact]
    public async Task Should_WriteNothing_When_AlreadySaved()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store);
        var first = (await store.SaveUploadAsync(Save(extraction), default)).Record!;
        var requests = await RequestCountAsync(store);
        var versions = (await ((ISavedRequestStore)store).VersionsAsync(first.Id, default)).Count;
        var events = (await ((ISavedRequestStore)store).EventsAsync(first.Id, default)).Count;
        var audit = await AuditCountAsync(store);

        await store.SaveUploadAsync(Save(extraction, edited: true), default);

        Assert.Equal(requests, await RequestCountAsync(store));
        Assert.Equal(versions, (await ((ISavedRequestStore)store).VersionsAsync(first.Id, default)).Count);
        Assert.Equal(events, (await ((ISavedRequestStore)store).EventsAsync(first.Id, default)).Count);
        Assert.Equal(audit, await AuditCountAsync(store));
        Assert.Equal(first.RowVersion, (await ((ISavedRequestStore)store).GetAsync(first.Id, default))!.RowVersion);
    }

    [ContractFact]
    public async Task Should_ReturnExpiredAndDelete_When_Expired()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store, Extraction(expires: Now.AddMinutes(-1)));

        var result = await store.SaveUploadAsync(Save(extraction), default);

        Assert.Equal(UploadSaveKind.Expired, result.Kind);
        Assert.Null(result.Record);
        Assert.Null(await store.GetExtractionAsync(extraction.Id, default));
        Assert.Equal(0, await RequestCountAsync(store));
    }

    [ContractFact]
    public async Task Should_ReturnExpired_When_ExtractionUnknown()
    {
        var store = CreateStore();

        var result = await store.SaveUploadAsync(Save(Extraction()), default);

        Assert.Equal(UploadSaveKind.Expired, result.Kind);
        Assert.Equal(0, await RequestCountAsync(store));
    }

    [ContractFact]
    public async Task Should_ReturnNotFound_When_OtherUser()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store);

        var result = await store.SaveUploadAsync(Save(extraction, user: "user-8"), default);

        Assert.Equal(UploadSaveKind.NotFound, result.Kind);
        Assert.Null(result.Record);
        Assert.Null(result.SavedRequestId);
        Assert.Null((await store.GetExtractionAsync(extraction.Id, default))!.SavedRequestId);
        Assert.Equal(0, await RequestCountAsync(store));
    }

    [ContractFact]
    public async Task Should_ReplayCreated_When_SameIdempotencyKey()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store);
        var first = await store.SaveUploadAsync(Save(extraction, key: "k-1"), default);

        var again = await store.SaveUploadAsync(Save(extraction, key: "k-1"), default);

        Assert.Equal(UploadSaveKind.Replayed, again.Kind);
        Assert.Equal(first.Record!.Id, again.Record!.Id);
        Assert.Equal(1, await RequestCountAsync(store));
    }

    [ContractFact]
    public async Task Should_ReturnKeyReused_When_SameKeyOtherBodyAndUnsavedExtraction()
    {
        var store = CreateStore();
        var first = await store.SaveUploadAsync(Save(await StoredAsync(store), key: "k-1"), default);
        var other = await StoredAsync(store, Extraction("Other.pdf"));

        var result = await store.SaveUploadAsync(Save(other, key: "k-1", reference: "R-OTHER"), default);

        Assert.Equal(UploadSaveKind.KeyReused, result.Kind);
        Assert.Equal(first.Record!.Id, result.Record!.Id);
        Assert.Null((await store.GetExtractionAsync(other.Id, default))!.SavedRequestId);
        Assert.Equal(1, await RequestCountAsync(store));
    }

    [ContractFact]
    public async Task Should_MatchFileName_When_QContainsIt()
    {
        var store = CreateStore();
        var upload = (await store.SaveUploadAsync(Save(await StoredAsync(store, Extraction("Statement-Q3_2026.pdf"))), default)).Record!;
        await PlainSaveAsync(store, Now);

        var page = await ((ISavedRequestV2Store)store).ListV2Async(
            new SavedRequestQueryV2(
                new SavedRequestQuery([], null, null, "statement-q3_2026", null, null, null, "-created", 1, 50),
                null, null, null, null, null, null, null),
            default);

        var row = Assert.Single(page.Items);
        Assert.Equal(upload.Id, row.Id);
        Assert.Equal("Statement-Q3_2026.pdf", row.Upload?.FileName);
    }

    [ContractFact]
    public async Task Should_ReturnShaMatchOutsideWindow_When_CandidatesLoaded()
    {
        var store = CreateStore();
        var old = Now.AddDays(-200);
        var oldExtraction = Extraction() with { ExtractedUtc = old.AddHours(-1), ExpiresUtc = old.AddHours(23) };
        var oldUpload = (await store.SaveUploadAsync(Save(await StoredAsync(store, oldExtraction), edited: true, now: old), default)).Record!;
        await PlainSaveAsync(store, old, "OLD-PLAIN");
        var recent = await PlainSaveAsync(store, Now.AddDays(-1));
        await StoredAsync(store, Extraction()); // an unsaved extraction of the same file counts for nothing

        var candidates = await store.UploadDuplicateCandidatesAsync(Sha, Now.AddDays(-90), default);

        Assert.Equal([recent, oldUpload.Id], candidates.Select(c => c.Id));
        var upload = candidates[1];
        Assert.Equal(("upload", Sha, "R-MARCH", SavedRequestStatus.Pending), (upload.Source, upload.UploadSha256, upload.Reference, upload.Status));
        Assert.Equal((old, old), (upload.ReceivedUtc, upload.CreatedUtc));
        Assert.Equal(new HashSet<string> { "fp-R-MARCH" }, upload.RowFingerprints);
        Assert.Equal(
            [new RowKey("deposit", "2026-03-01", 13.50m), new RowKey("check", "2026-03-02", 40.00m), new RowKey("check", "2026-03-03", 5.00m)],
            upload.RowKeys);
        Assert.Equal(("other", (string?)null), (candidates[0].Source, candidates[0].UploadSha256));
    }

    [ContractFact]
    public async Task Should_ReturnOneSavedRequest_When_TwoSavesRace()
    {
        var store = CreateStore();
        var extraction = await StoredAsync(store);

        var results = await Task.WhenAll(
            Task.Run(() => store.SaveUploadAsync(Save(extraction), default)),
            Task.Run(() => store.SaveUploadAsync(Save(extraction, edited: true), default)));

        Assert.Equal(
            [UploadSaveKind.Created, UploadSaveKind.AlreadySaved],
            results.Select(r => r.Kind).OrderBy(k => k));
        var created = results.Single(r => r.Kind == UploadSaveKind.Created).Record!;
        Assert.Equal(created.Id, results.Single(r => r.Kind == UploadSaveKind.AlreadySaved).SavedRequestId);
        Assert.Equal(1, await RequestCountAsync(store));
        Assert.Equal(created.Id, (await store.GetExtractionAsync(extraction.Id, default))!.SavedRequestId);
    }
}
