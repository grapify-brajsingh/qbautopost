using System.Text.Json;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1105: what every <see cref="ISavedRequestStore"/> must do with the dashboard v2 columns (spec
/// saved-requests-dashboard-v2 4.1, 5.3, 6.4): save metadata kept from the first save, both snapshots, the v2
/// attention flag, and the skip and problem counts. Store-agnostic, so the fake and the EF Core store run the same
/// tests; on a <see cref="RequiresPostgresAttribute"/> subclass they skip when Docker cannot provide a PostgreSQL.
/// </summary>
public abstract class DashboardV2StoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Actor = "user:7";

    protected abstract ISavedRequestStore CreateStore();

    // ---- builders ------------------------------------------------------------------------------------------

    private static string Row(string externalId, string? post = null) =>
        $$"""{"externalId":"{{externalId}}","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"{{(post is null ? "" : ",\"post\":" + post)}}}""";

    private static string Body(params string[] rows) =>
        $$"""{"reference":"R1","controlTotal":{{10m * rows.Length:0.00}},"transactions":[{{string.Join(',', rows)}}]}""";

    private static ValidationSnapshot Snapshot(
        ValidationState state = ValidationState.Ok, int version = 1, IReadOnlyList<SyncPostFinding>? findings = null,
        IReadOnlyList<string>? errors = null) =>
        new(state, T0, version, null, findings ?? [], errors ?? [], null);

    private static SyncPostFinding Finding(int index) => new(index, $"e{index}", "unknown-account", "no such account", null, null);

    private static NewSavedRequest NewRequest(
        string? body = null, ValidationSnapshot? snapshot = null, ValidationSnapshot? snapshotV2 = null, SaveMetadata? metadata = null)
    {
        body ??= Body(Row("e1"));
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var read = SavedPayloadReader.Read(payload);
        return new NewSavedRequest(
            "api-client:n8n", "api-client:n8n", body, payload, read, PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, snapshot ?? Snapshot(), new DuplicateMatch(null, []), T0)
        {
            Metadata = metadata ?? SaveMetadata.None,
            SnapshotV2 = snapshotV2,
        };
    }

    private static NewVersion NewVersionOf(string body, ValidationSnapshot? snapshotV2 = null)
    {
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var read = SavedPayloadReader.Read(payload);
        return new NewVersion(
            payload, read, PayloadCanonicalizer.Sha256(payload), new HashSet<string>(), [], null,
            Snapshot(ValidationState.Ok, 2), new DuplicateMatch(null, []), Actor, T0.AddMinutes(1))
        {
            SnapshotV2 = snapshotV2,
        };
    }

    private static SaveMetadata FullMetadata() => new(
        "Statement for October", new DateTime(2026, 10, 27, 8, 30, 0, DateTimeKind.Utc), "ops@example.com",
        "<m1@example.com>", Guid.Parse("6f1c2a4e-9b7d-4c3e-8a21-0d5e6f7a8b9c"), ["$.pushId: not a UUID"]);

    private static async Task<SavedRequest> CreateAsync(ISavedRequestStore store, NewSavedRequest request) =>
        (await store.CreateAsync(request, default)).Record;

    private static void AssertSameMetadata(SaveMetadata expected, SaveMetadata actual)
    {
        Assert.Equal(expected.EmailSubject, actual.EmailSubject);
        Assert.Equal(expected.EmailReceivedUtc, actual.EmailReceivedUtc);
        Assert.Equal(expected.EmailFrom, actual.EmailFrom);
        Assert.Equal(expected.EmailMessageId, actual.EmailMessageId);
        Assert.Equal(expected.PushId, actual.PushId);
        Assert.Equal(expected.MetadataErrors, actual.MetadataErrors);
    }

    // ---- tests ---------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_PersistMetadata_When_Created()
    {
        var store = CreateStore();
        var metadata = FullMetadata();

        var created = await CreateAsync(store, NewRequest(metadata: metadata));
        var read = (await store.GetAsync(created.Id, default))!;

        AssertSameMetadata(metadata, created.Metadata);
        AssertSameMetadata(metadata, read.Metadata);
    }

    [ContractFact]
    public async Task Should_NeverChangeMetadata_When_VersionAppended()
    {
        var store = CreateStore();
        var metadata = FullMetadata();
        var created = await CreateAsync(store, NewRequest(metadata: metadata));

        var edited = await store.AppendVersionAsync(created.Id, created.RowVersion, NewVersionOf(Body(Row("e1"), Row("e2"))), default);

        Assert.Equal(MutationKind.Ok, edited.Kind);
        AssertSameMetadata(metadata, edited.Record!.Metadata);
        AssertSameMetadata(metadata, (await store.GetAsync(created.Id, default))!.Metadata);
    }

    [ContractFact]
    public async Task Should_StoreBothSnapshots_When_Created()
    {
        var store = CreateStore();
        var v1 = Snapshot(ValidationState.Ok);
        var v2 = Snapshot(ValidationState.Held, findings: [Finding(0)], errors: ["$.transactions[1].post: not a boolean"]);

        var created = await CreateAsync(store, NewRequest(snapshot: v1, snapshotV2: v2));
        var read = (await store.GetAsync(created.Id, default))!;

        Assert.Equal(ValidationState.Ok, read.Validation!.State);
        Assert.False(read.NeedsAttention);
        Assert.Equal(ValidationState.Held, read.ValidationV2!.State);
        Assert.Equal(new[] { "$.transactions[1].post: not a boolean" }, read.ValidationV2.Errors);
        Assert.Equal("e0", Assert.Single(read.ValidationV2.Validation).ExternalId);
        Assert.True(read.NeedsAttentionV2);
        Assert.Equal(2, read.ProblemCount);
    }

    [ContractFact]
    public async Task Should_CopyV1Snapshot_When_SnapshotV2Null()
    {
        var store = CreateStore();
        var v1 = Snapshot(ValidationState.Held, findings: [Finding(0)]);

        var created = await CreateAsync(store, NewRequest(snapshot: v1, snapshotV2: null));
        var read = (await store.GetAsync(created.Id, default))!;

        Assert.Equal(ValidationState.Held, read.ValidationV2!.State);
        Assert.Equal(v1.VersionNumber, read.ValidationV2.VersionNumber);
        Assert.Equal(v1.ValidatedUtc, read.ValidationV2.ValidatedUtc);
        Assert.Equal("e0", Assert.Single(read.ValidationV2.Validation).ExternalId);
        Assert.Equal(read.NeedsAttention, read.NeedsAttentionV2);
        Assert.Equal(1, read.ProblemCount);
    }

    [ContractFact]
    public async Task Should_SetNeedsAttentionV2_When_MetadataErrors()
    {
        var store = CreateStore();
        var metadata = SaveMetadata.None with { MetadataErrors = [SaveMetadataReader.PushIdError] };

        var created = await CreateAsync(store, NewRequest(snapshot: Snapshot(ValidationState.Ok), metadata: metadata));
        var read = (await store.GetAsync(created.Id, default))!;

        Assert.False(read.NeedsAttention);
        Assert.Equal(ValidationState.Ok, read.ValidationV2!.State);
        Assert.True(read.NeedsAttentionV2);
    }

    [ContractFact]
    public async Task Should_RefreshSkipCount_When_VersionAppended()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRequest(Body(Row("e1", "false"), Row("e2"))));

        var edited = await store.AppendVersionAsync(
            created.Id, created.RowVersion, NewVersionOf(Body(Row("e1", "false"), Row("e2", "false"), Row("e3", "true"))), default);

        Assert.Equal(1, created.SkipCount);
        Assert.Equal(2, edited.Record!.SkipCount);
        Assert.Equal(2, (await store.GetAsync(created.Id, default))!.SkipCount);
    }

    [ContractFact]
    public async Task Should_RefreshProblemCount_When_ValidationSet()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRequest());
        var v2 = Snapshot(ValidationState.Refused, findings: [Finding(0)], errors: ["control-total-mismatch", "nothing-to-post"]);

        var result = await store.SetValidationAsync(
            created.Id, Snapshot(ValidationState.Ok), v2, new DuplicateMatch(null, []), Actor, T0.AddMinutes(2), default);
        var read = (await store.GetAsync(created.Id, default))!;

        Assert.Equal(MutationKind.Ok, result.Kind);
        Assert.Equal(0, created.ProblemCount);
        Assert.Equal(3, read.ProblemCount);
        Assert.Equal(ValidationState.Refused, read.ValidationV2!.State);
        Assert.True(read.NeedsAttentionV2);
        Assert.Equal(ValidationState.Ok, read.Validation!.State);
        Assert.False(read.NeedsAttention);
    }
}
