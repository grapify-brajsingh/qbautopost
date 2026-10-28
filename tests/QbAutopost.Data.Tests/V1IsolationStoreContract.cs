using System.Text.Json;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>
/// M11 final review (ruling R7 and spec 5.3): the v1 store reads stay what they were. <c>GetAsync</c> never fills the v2
/// summaries, and v1 <c>ListAsync</c> filters on the v1 <c>needs_attention</c> column while <c>ListV2Async</c> uses
/// <c>needs_attention_v2</c>. Store-agnostic, so the fake and the EF Core store run the same tests.
/// </summary>
public abstract class V1IsolationStoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Client = "api-client:n8n";
    private const string Body =
        """{"reference":"R1","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";

    protected abstract ISavedRequestStore CreateStore();

    private static ValidationSnapshot Snapshot(ValidationState state) => new(state, T0, 1, null, [], [], null);

    private static NewSavedRequest NewRequest(ValidationSnapshot snapshot, ValidationSnapshot snapshotV2)
    {
        var payload = JsonDocument.Parse(Body).RootElement.Clone();
        return new NewSavedRequest(
            Client, Client, Body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, snapshot, new DuplicateMatch(null, []), T0)
        {
            Metadata = SaveMetadata.None,
            SnapshotV2 = snapshotV2,
        };
    }

    [ContractFact]
    public async Task Should_LeaveV1GetUnfilled_When_PostedThroughV2()
    {
        var store = CreateStore();
        var created = (await store.CreateAsync(NewRequest(Snapshot(ValidationState.Ok), Snapshot(ValidationState.Ok)), default)).Record;
        var attempt = (await store.TryClaimAsync(created.Id, null, Client, false, T0.AddMinutes(1), default)).AttemptId!.Value;
        await store.CompleteAttemptAsync(
            attempt, new AttemptCompletion(AttemptOutcome.Posted, SavedRequestStatus.Posted, 200, "P-1", null, null, null, T0.AddMinutes(2)), default);

        var v1 = (await store.GetAsync(created.Id, default))!;
        var v2 = (await Assert.IsAssignableFrom<ISavedRequestV2Store>(store).GetV2Async(created.Id, default))!;

        Assert.Null(v1.PostSummary);
        Assert.Equal(0, v1.HistoryCount);
        Assert.NotNull(v2.PostSummary);
        Assert.True(v2.HistoryCount > 0);
    }

    [ContractFact]
    public async Task Should_FilterV1AndV2OnTheirOwnColumn_When_NeedsAttentionDiffers()
    {
        var store = CreateStore();
        var v2Only = (await store.CreateAsync(NewRequest(Snapshot(ValidationState.Ok), Snapshot(ValidationState.Held)), default)).Record;
        var v1Only = (await store.CreateAsync(NewRequest(Snapshot(ValidationState.Held), Snapshot(ValidationState.Ok)), default)).Record;
        var query = new SavedRequestQuery([], true, null, null, null, null, null, "-created", 1, 50);

        var v1Ids = (await store.ListAsync(query, default)).Items.Select(i => i.Id).ToArray();
        var v2Ids = (await Assert.IsAssignableFrom<ISavedRequestV2Store>(store)
            .ListV2Async(new SavedRequestQueryV2(query, null, null, null, null, null, null, null), default)).Items.Select(i => i.Id).ToArray();

        Assert.Equal([v1Only.Id], v1Ids);
        Assert.Equal([v2Only.Id], v2Ids);
    }
}
