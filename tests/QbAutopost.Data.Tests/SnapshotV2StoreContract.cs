using System.Text.Json;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1112: the v2 snapshot on the write paths an edit and a revalidation take (dashboard v2 spec 5.2, 5.3, 6.4). A
/// v2 snapshot for another version is stale, like a v1 one; a metadata error keeps <c>needs_attention_v2</c> set
/// whatever the snapshot says (Q-95); an append without a v2 snapshot copies the v1 one. Store-agnostic, so the fake
/// and the EF Core store run the same tests.
/// </summary>
public abstract class SnapshotV2StoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Actor = "user:7";
    private const string Body = """{"reference":"R1","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
    private const string EditedBody = """{"reference":"R1","controlTotal":12.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":12.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";

    protected abstract ISavedRequestStore CreateStore();

    private static ValidationSnapshot Snapshot(ValidationState state, int version, IReadOnlyList<SyncPostFinding>? findings = null) =>
        new(state, T0, version, null, findings ?? [], [], null);

    private static DuplicateMatch NoDuplicate() => new(null, []);

    private static async Task<SavedRequest> CreateAsync(ISavedRequestStore store, SaveMetadata metadata)
    {
        var payload = JsonDocument.Parse(Body).RootElement.Clone();
        var read = SavedPayloadReader.Read(payload);
        var request = new NewSavedRequest(
            "api-client:n8n", "api-client:n8n", Body, payload, read, PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, Snapshot(ValidationState.Ok, 1), NoDuplicate(), T0)
        {
            Metadata = metadata,
            SnapshotV2 = Snapshot(ValidationState.Ok, 1),
        };
        return (await store.CreateAsync(request, default)).Record;
    }

    private static NewVersion Version2(ValidationSnapshot snapshot, ValidationSnapshot? snapshotV2)
    {
        var payload = JsonDocument.Parse(EditedBody).RootElement.Clone();
        return new NewVersion(
            payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload), new HashSet<string>(), [],
            null, snapshot, NoDuplicate(), Actor, T0.AddMinutes(1))
        {
            SnapshotV2 = snapshotV2,
        };
    }

    private static SaveMetadata PushIdError() => SaveMetadata.None with { MetadataErrors = [SaveMetadataReader.PushIdError] };

    [ContractFact]
    public async Task Should_AnswerStale_When_SnapshotV2IsForAnotherVersion()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, SaveMetadata.None);

        var result = await store.SetValidationAsync(
            created.Id, Snapshot(ValidationState.Ok, 1), Snapshot(ValidationState.Held, 2), NoDuplicate(), Actor,
            T0.AddMinutes(2), default);
        var read = (await store.GetAsync(created.Id, default))!;

        Assert.Equal(MutationKind.Stale, result.Kind);
        Assert.Equal(created.RowVersion, read.RowVersion);
        Assert.Equal(ValidationState.Ok, read.ValidationV2!.State);
    }

    [ContractFact]
    public async Task Should_KeepNeedsAttentionV2_When_VersionAppendedWithMetadataErrors()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, PushIdError());

        var edited = await store.AppendVersionAsync(
            created.Id, created.RowVersion, Version2(Snapshot(ValidationState.Ok, 2), Snapshot(ValidationState.Ok, 2)), default);
        var read = (await store.GetAsync(created.Id, default))!;

        Assert.Equal(MutationKind.Ok, edited.Kind);
        Assert.Equal(ValidationState.Ok, read.ValidationV2!.State);
        Assert.True(read.NeedsAttentionV2);
        Assert.False(read.NeedsAttention);
    }

    [ContractFact]
    public async Task Should_KeepNeedsAttentionV2_When_ValidationSetWithMetadataErrors()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, PushIdError());

        var result = await store.SetValidationAsync(
            created.Id, Snapshot(ValidationState.Ok, 1), Snapshot(ValidationState.Ok, 1), NoDuplicate(), Actor,
            T0.AddMinutes(2), default);
        var read = (await store.GetAsync(created.Id, default))!;

        Assert.Equal(MutationKind.Ok, result.Kind);
        Assert.Equal(ValidationState.Ok, read.ValidationV2!.State);
        Assert.True(read.NeedsAttentionV2);
        Assert.False(read.NeedsAttention);
    }

    [ContractFact]
    public async Task Should_CopyV1Snapshot_When_VersionAppendedWithoutSnapshotV2()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, SaveMetadata.None);
        var finding = new SyncPostFinding(0, "e1", "unknown-account", "no such account", null, null);

        var edited = await store.AppendVersionAsync(
            created.Id, created.RowVersion, Version2(Snapshot(ValidationState.Held, 2, [finding]), snapshotV2: null), default);
        var read = (await store.GetAsync(created.Id, default))!;

        Assert.Equal(MutationKind.Ok, edited.Kind);
        Assert.Equal(ValidationState.Held, read.ValidationV2!.State);
        Assert.Equal(2, read.ValidationV2.VersionNumber);
        Assert.Equal("e1", Assert.Single(read.ValidationV2.Validation).ExternalId);
        Assert.True(read.NeedsAttentionV2);
        Assert.Equal(1, read.ProblemCount);
    }
}
