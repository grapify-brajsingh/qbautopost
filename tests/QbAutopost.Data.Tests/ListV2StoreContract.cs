using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1107: the dashboard v2 reads every <see cref="ISavedRequestStore"/> serves through
/// <see cref="ISavedRequestV2Store"/> (spec saved-requests-dashboard-v2 5.2 search, 5.3, 8.4, 9.1-9.3), and the
/// <c>postSummary</c> and <c>historyCount</c> that <see cref="ISavedRequestV2Store.GetV2Async"/> fills (ruling R7). Store-agnostic,
/// so the fake and the EF Core store run the same tests.
/// </summary>
public abstract class ListV2StoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Client = "api-client:n8n";
    private const string Admin = "user-7";
    private static readonly Guid PushA = Guid.Parse("6f1c2a4e-9b7d-4c3e-8a21-0d5e6f7a8b9c");
    private static readonly Guid PushB = Guid.Parse("0a0b0c0d-1e1f-4a2b-9c3d-4e5f60718293");

    protected abstract ISavedRequestStore CreateStore();

    private static ISavedRequestV2Store V2(ISavedRequestStore store) => Assert.IsAssignableFrom<ISavedRequestV2Store>(store);

    private static ISavedRequestAuditStore Feed(ISavedRequestStore store) => Assert.IsAssignableFrom<ISavedRequestAuditStore>(store);

    // ---- builders ------------------------------------------------------------------------------------------

    private static string Row(string externalId, string payee = "Acme") =>
        $$"""{"externalId":"{{externalId}}","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"{{payee}}","memo":"rent"}""";

    private static string Body(string reference = "R1", string? controlTotal = "10.00", int rows = 1)
    {
        var total = controlTotal is null ? "" : $"\"controlTotal\":{controlTotal},";
        var list = string.Join(',', Enumerable.Range(1, rows).Select(i => Row($"e{i}")));
        return $$"""{"reference":"{{reference}}",{{total}}"transactions":[{{list}}]}""";
    }

    private static ValidationSnapshot Snapshot(
        ValidationState state = ValidationState.Ok, int version = 1, int findings = 0) =>
        new(state, T0, version, null,
            Enumerable.Range(0, findings).Select(i => new SyncPostFinding(i, $"e{i}", "unknown-account", "no such account", null, null)).ToList(),
            [], null);

    private static NewSavedRequest NewRequest(
        string? body = null, DateTime? createdUtc = null, SaveMetadata? metadata = null, ValidationSnapshot? snapshot = null,
        ValidationSnapshot? snapshotV2 = null, DuplicateMatch? duplicate = null)
    {
        body ??= Body();
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        return new NewSavedRequest(
            Client, Client, body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, snapshot ?? Snapshot(), duplicate ?? new DuplicateMatch(null, []), createdUtc ?? T0)
        {
            Metadata = metadata ?? SaveMetadata.None,
            SnapshotV2 = snapshotV2,
        };
    }

    private static SaveMetadata Email(DateTime? receivedUtc = null, string? subject = null, string? from = null, Guid? pushId = null) =>
        SaveMetadata.None with { EmailReceivedUtc = receivedUtc, EmailSubject = subject, EmailFrom = from, PushId = pushId };

    private static SavedRequestQuery Base(
        IReadOnlyList<SavedRequestStatus>? statuses = null, bool? needsAttention = null, bool? duplicate = null, string? q = null,
        string sort = "-created") =>
        new(statuses ?? [], needsAttention, duplicate, q, null, null, null, sort, 1, 50);

    private static SavedRequestQueryV2 Query(
        SavedRequestQuery? @base = null, DateOnly? receivedFrom = null, DateOnly? receivedTo = null, string? emailFrom = null,
        string? reference = null, decimal? minTotal = null, decimal? maxTotal = null, bool? hasErrors = null) =>
        new(@base ?? Base(), receivedFrom, receivedTo, emailFrom, reference, minTotal, maxTotal, hasErrors);

    private static async Task<SavedRequest> CreateAsync(ISavedRequestStore store, NewSavedRequest request) =>
        (await store.CreateAsync(request, default)).Record;

    private static async Task<long[]> IdsAsync(ISavedRequestStore store, SavedRequestQueryV2 query) =>
        (await V2(store).ListV2Async(query, default)).Items.Select(i => i.Id).ToArray();

    private static async Task<long> ClaimAsync(ISavedRequestStore store, long id, DateTime at) =>
        (await store.TryClaimAsync(id, null, Client, false, at, default)).AttemptId!.Value;

    private static Task CompleteAsync(
        ISavedRequestStore store, long attemptId, AttemptOutcome outcome, SavedRequestStatus next, DateTime at,
        string? postId = null, JsonNode? counts = null) =>
        store.CompleteAttemptAsync(attemptId, new AttemptCompletion(outcome, next, 200, postId, null, counts, null, at), default);

    // ---- search (5.2, 9.2) -----------------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_MatchSubject_When_QGiven()
    {
        var store = CreateStore();
        var a = await CreateAsync(store, NewRequest(metadata: Email(subject: "Statement for October")));
        await CreateAsync(store, NewRequest(metadata: Email(subject: "Invoices")));
        await CreateAsync(store, NewRequest());

        Assert.Equal([a.Id], await IdsAsync(store, Query(Base(q: "  OCTOBER "))));
    }

    [ContractTheory]
    [InlineData("6F1C2A4E-9B7D")]
    [InlineData("6f1c2a4e9b7d4c")]
    [InlineData("6f1c2a4e-9b7d-4c3e-8a21-0d5e6f7a8b9c")]
    public async Task Should_MatchPushIdPrefix_When_QIsHexWithOrWithoutDashes(string q)
    {
        var store = CreateStore();
        var a = await CreateAsync(store, NewRequest(metadata: Email(pushId: PushA)));
        await CreateAsync(store, NewRequest(metadata: Email(pushId: PushB)));
        await CreateAsync(store, NewRequest());

        Assert.Equal([a.Id], await IdsAsync(store, Query(Base(q: q))));
    }

    [ContractFact]
    public async Task Should_NotMatchPushIdInTheMiddle_When_QIsHex()
    {
        var store = CreateStore();
        await CreateAsync(store, NewRequest(metadata: Email(pushId: PushA)));

        Assert.Empty(await IdsAsync(store, Query(Base(q: "9b7d"))));
    }

    // ---- sorts (9.2) -----------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_SortByReceivedFallingBackToCreated_When_SortReceived()
    {
        var store = CreateStore();
        var late = await CreateAsync(store, NewRequest(createdUtc: T0, metadata: Email(receivedUtc: T0.AddHours(5))));
        var noEmail = await CreateAsync(store, NewRequest(createdUtc: T0.AddHours(1)));
        var early = await CreateAsync(store, NewRequest(createdUtc: T0.AddHours(2), metadata: Email(receivedUtc: T0.AddHours(-1))));

        Assert.Equal([early.Id, noEmail.Id, late.Id], await IdsAsync(store, Query(Base(sort: "received"))));
        Assert.Equal([late.Id, noEmail.Id, early.Id], await IdsAsync(store, Query(Base(sort: "-received"))));
    }

    [ContractFact]
    public async Task Should_SortByTotalWithNullsLast_When_SortTotal()
    {
        var store = CreateStore();
        var none = await CreateAsync(store, NewRequest(Body(controlTotal: null)));
        var ten = await CreateAsync(store, NewRequest(Body(controlTotal: "10.00")));
        var thirty = await CreateAsync(store, NewRequest(Body(controlTotal: "30.00")));
        Assert.Null(none.ControlTotal);

        Assert.Equal([ten.Id, thirty.Id, none.Id], await IdsAsync(store, Query(Base(sort: "total"))));
        Assert.Equal([thirty.Id, ten.Id, none.Id], await IdsAsync(store, Query(Base(sort: "-total"))));
    }

    [ContractFact]
    public async Task Should_SortByRowCountWithIdTieBreak_When_SortRows()
    {
        var store = CreateStore();
        var three = await CreateAsync(store, NewRequest(Body(rows: 3, controlTotal: "30.00")));
        var oneA = await CreateAsync(store, NewRequest(Body(rows: 1)));
        var oneB = await CreateAsync(store, NewRequest(Body(rows: 1)));

        Assert.Equal([oneA.Id, oneB.Id, three.Id], await IdsAsync(store, Query(Base(sort: "rows"))));
        Assert.Equal([three.Id, oneB.Id, oneA.Id], await IdsAsync(store, Query(Base(sort: "-rows"))));
    }

    // ---- filters (9.2) ---------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_FilterReceivedRangeInclusive_When_Given()
    {
        var store = CreateStore();
        var day = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        await CreateAsync(store, NewRequest(createdUtc: day.AddHours(9), metadata: Email(receivedUtc: day.AddSeconds(-1))));
        var createdThatDay = await CreateAsync(store, NewRequest(createdUtc: day.AddHours(9)));
        var receivedLastSecond = await CreateAsync(
            store, NewRequest(createdUtc: day.AddDays(2), metadata: Email(receivedUtc: day.AddDays(1).AddSeconds(-1))));
        var receivedFirstSecond = await CreateAsync(store, NewRequest(createdUtc: day.AddDays(2), metadata: Email(receivedUtc: day)));
        await CreateAsync(store, NewRequest(createdUtc: day.AddDays(1)));

        var ids = await IdsAsync(store, Query(Base(sort: "received"), receivedFrom: new DateOnly(2026, 10, 28), receivedTo: new DateOnly(2026, 10, 28)));

        Assert.Equal([receivedFirstSecond.Id, createdThatDay.Id, receivedLastSecond.Id], ids);
    }

    [ContractFact]
    public async Task Should_ExcludeNullTotal_When_MinTotalGiven()
    {
        var store = CreateStore();
        var ten = await CreateAsync(store, NewRequest(Body(controlTotal: "10.00")));
        var thirty = await CreateAsync(store, NewRequest(Body(controlTotal: "30.00")));
        await CreateAsync(store, NewRequest(Body(controlTotal: "9.99")));
        var none = await CreateAsync(store, NewRequest(Body(controlTotal: null)));
        Assert.Null(none.ControlTotal);

        Assert.Equal([ten.Id, thirty.Id], await IdsAsync(store, Query(Base(sort: "total"), minTotal: 10.00m)));
    }

    [ContractFact]
    public async Task Should_FilterTotalRangeInclusive_When_MaxTotalGiven()
    {
        var store = CreateStore();
        var ten = await CreateAsync(store, NewRequest(Body(controlTotal: "10.00")));
        await CreateAsync(store, NewRequest(Body(controlTotal: "30.00")));
        await CreateAsync(store, NewRequest(Body(controlTotal: null)));

        Assert.Equal([ten.Id], await IdsAsync(store, Query(Base(sort: "total"), minTotal: 10.00m, maxTotal: 10.00m)));
    }

    [ContractFact]
    public async Task Should_FilterEmailFromContainsIgnoringCase_When_Given()
    {
        var store = CreateStore();
        var a = await CreateAsync(store, NewRequest(metadata: Email(from: "Ops@Example.com")));
        await CreateAsync(store, NewRequest(metadata: Email(from: "someone@else.org")));
        await CreateAsync(store, NewRequest());

        Assert.Equal([a.Id], await IdsAsync(store, Query(emailFrom: "example.COM")));
    }

    [ContractFact]
    public async Task Should_FilterReferenceExactly_When_Given()
    {
        var store = CreateStore();
        var a = await CreateAsync(store, NewRequest(Body(reference: "INV-1")));
        await CreateAsync(store, NewRequest(Body(reference: "INV-10")));
        await CreateAsync(store, NewRequest(Body(reference: "inv-1")));

        Assert.Equal([a.Id], await IdsAsync(store, Query(reference: "INV-1")));
    }

    [ContractFact]
    public async Task Should_UseNeedsAttentionV2_When_FilteringAndCounting()
    {
        var store = CreateStore();
        var v2Only = await CreateAsync(
            store, NewRequest(snapshot: Snapshot(ValidationState.Ok), snapshotV2: Snapshot(ValidationState.Held)));
        var v1Only = await CreateAsync(
            store, NewRequest(snapshot: Snapshot(ValidationState.Held), snapshotV2: Snapshot(ValidationState.Ok)));

        Assert.Equal([v2Only.Id], await IdsAsync(store, Query(Base(needsAttention: true))));
        Assert.Equal([v1Only.Id], await IdsAsync(store, Query(Base(needsAttention: false))));
        Assert.Equal(new FlagCounts(1, 1), (await V2(store).StatsV2Async(default)).NeedsAttention);
    }

    [ContractFact]
    public async Task Should_FilterHasErrors_When_ProblemsOrAttention()
    {
        var store = CreateStore();
        var problems = await CreateAsync(store, NewRequest(snapshotV2: Snapshot(ValidationState.Ok, findings: 1)));
        var attention = await CreateAsync(
            store, NewRequest(metadata: SaveMetadata.None with { MetadataErrors = [SaveMetadataReader.PushIdError] }));
        var clean = await CreateAsync(store, NewRequest());

        Assert.Equal([attention.Id, problems.Id], await IdsAsync(store, Query(hasErrors: true)));
        Assert.Equal([clean.Id], await IdsAsync(store, Query(hasErrors: false)));
    }

    [ContractFact]
    public async Task Should_CombineStatusAndFlags_When_BothGiven()
    {
        var store = CreateStore();
        var held = Snapshot(ValidationState.Held, findings: 1);
        var dup = new DuplicateMatch(null, ["same-reference"]);
        var pendingFlagged = await CreateAsync(store, NewRequest(snapshotV2: held, duplicate: dup));
        var discardedFlagged = await CreateAsync(store, NewRequest(snapshotV2: held, duplicate: dup));
        await store.ChangeStatusAsync(
            discardedFlagged.Id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Admin, T0.AddMinutes(1),
            SavedRequestEvents.Discarded, null, default);
        await CreateAsync(store, NewRequest());
        var pending = Base(statuses: [SavedRequestStatus.Pending]);

        Assert.Equal([pendingFlagged.Id], await IdsAsync(store, Query(pending with { NeedsAttention = true })));
        Assert.Equal([pendingFlagged.Id], await IdsAsync(store, Query(pending with { Duplicate = true })));
        Assert.Equal([pendingFlagged.Id], await IdsAsync(store, Query(pending, hasErrors: true)));
    }

    // ---- stats (9.3) -----------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_CountPendingAndAll_When_StatsV2()
    {
        var store = CreateStore();
        var dup = new DuplicateMatch(null, ["same-reference"]);
        // pending: attention + duplicate; pending: problems only; discarded: attention + duplicate; posted: clean.
        await CreateAsync(store, NewRequest(snapshotV2: Snapshot(ValidationState.Held), duplicate: dup));
        await CreateAsync(store, NewRequest(snapshotV2: Snapshot(ValidationState.Ok, findings: 2)));
        var discarded = await CreateAsync(store, NewRequest(snapshotV2: Snapshot(ValidationState.Refused), duplicate: dup));
        await store.ChangeStatusAsync(
            discarded.Id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Admin, T0.AddMinutes(1),
            SavedRequestEvents.Discarded, null, default);
        var posted = await CreateAsync(store, NewRequest());
        await CompleteAsync(
            store, await ClaimAsync(store, posted.Id, T0.AddMinutes(2)), AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddMinutes(3));

        var stats = await V2(store).StatsV2Async(default);

        Assert.Equal(4, stats.Total);
        Assert.Equal(
            new Dictionary<SavedRequestStatus, int>
            {
                [SavedRequestStatus.Pending] = 2, [SavedRequestStatus.Posting] = 0, [SavedRequestStatus.Posted] = 1,
                [SavedRequestStatus.Partial] = 0, [SavedRequestStatus.Unknown] = 0, [SavedRequestStatus.Discarded] = 1,
            },
            stats.ByStatus.OrderBy(p => p.Key).ToDictionary());
        Assert.Equal(new FlagCounts(1, 2), stats.NeedsAttention);
        Assert.Equal(new FlagCounts(1, 2), stats.Duplicate);
        Assert.Equal(new FlagCounts(2, 3), stats.HasErrors);
    }

    // ---- postSummary (9.1) -----------------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_FillPostSummaryFromPostedAttempt_When_Posted()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRequest());
        var failed = await ClaimAsync(store, created.Id, T0.AddMinutes(1));
        await CompleteAsync(store, failed, AttemptOutcome.Failed, SavedRequestStatus.Pending, T0.AddMinutes(2), "P-0");
        var attempt = await ClaimAsync(store, created.Id, T0.AddMinutes(3));
        await CompleteAsync(
            store, attempt, AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddMinutes(4), "P-1",
            new JsonObject { ["posted"] = 3, ["skipped"] = 1 });

        var expected = new PostSummary("P-1", 3, T0.AddMinutes(4), Client);
        Assert.Equal(expected, (await V2(store).GetV2Async(created.Id, default))!.PostSummary);
        Assert.Equal(expected, Assert.Single((await V2(store).ListV2Async(Query(), default)).Items).PostSummary);
    }

    [ContractFact]
    public async Task Should_FillPostSummaryFromResolution_When_ResolvedPosted()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRequest());
        var attempt = await ClaimAsync(store, created.Id, T0.AddMinutes(1));
        await CompleteAsync(store, attempt, AttemptOutcome.Unknown, SavedRequestStatus.Unknown, T0.AddMinutes(2));
        await store.ResolveAsync(created.Id, ResolveOutcome.Posted, "Checked in QuickBooks", Admin, T0.AddMinutes(3), default);

        var summary = (await V2(store).GetV2Async(created.Id, default))!.PostSummary;

        Assert.Equal(new PostSummary(null, null, T0.AddMinutes(3), Admin), summary);
        Assert.Equal(summary, Assert.Single((await V2(store).ListV2Async(Query(), default)).Items).PostSummary);
    }

    [ContractFact]
    public async Task Should_LeavePostSummaryNull_When_NeverPosted()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRequest());
        await CompleteAsync(
            store, await ClaimAsync(store, created.Id, T0.AddMinutes(1)), AttemptOutcome.Failed, SavedRequestStatus.Pending,
            T0.AddMinutes(2));

        Assert.Null((await V2(store).GetV2Async(created.Id, default))!.PostSummary);
        Assert.Null(Assert.Single((await V2(store).ListV2Async(Query(), default)).Items).PostSummary);
    }

    // ---- otherUnpostedPushes (9.1) ---------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_ListOtherUnpostedPushes_When_SameReference()
    {
        var store = CreateStore();
        var self = await CreateAsync(store, NewRequest(Body("R1"), T0));
        var pendingLater = await CreateAsync(
            store, NewRequest(Body("R1"), T0.AddMinutes(1), Email(receivedUtc: T0.AddHours(3), pushId: PushA)));
        var posting = await CreateAsync(store, NewRequest(Body("R1"), T0.AddMinutes(2)));
        await ClaimAsync(store, posting.Id, T0.AddMinutes(10));
        var partial = await CreateAsync(store, NewRequest(Body("R1"), T0.AddMinutes(3)));
        await CompleteAsync(
            store, await ClaimAsync(store, partial.Id, T0.AddMinutes(11)), AttemptOutcome.Partial, SavedRequestStatus.Partial,
            T0.AddMinutes(12));
        var unknownEarly = await CreateAsync(
            store, NewRequest(Body("R1"), T0.AddMinutes(4), Email(receivedUtc: T0.AddHours(-2), pushId: PushB)));
        await CompleteAsync(
            store, await ClaimAsync(store, unknownEarly.Id, T0.AddMinutes(13)), AttemptOutcome.Unknown, SavedRequestStatus.Unknown,
            T0.AddMinutes(14));
        var posted = await CreateAsync(store, NewRequest(Body("R1"), T0.AddMinutes(5)));
        await CompleteAsync(
            store, await ClaimAsync(store, posted.Id, T0.AddMinutes(15)), AttemptOutcome.Posted, SavedRequestStatus.Posted,
            T0.AddMinutes(16));
        var discarded = await CreateAsync(store, NewRequest(Body("R1"), T0.AddMinutes(6)));
        await store.ChangeStatusAsync(
            discarded.Id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Admin, T0.AddMinutes(17),
            SavedRequestEvents.Discarded, null, default);
        await CreateAsync(store, NewRequest(Body("R2"), T0.AddMinutes(7)));
        await CreateAsync(store, NewRequest(Body("r1"), T0.AddMinutes(8)));

        var others = await V2(store).OtherUnpostedPushesAsync(self.Id, default);

        Assert.Equal(
            [
                new OtherPush(unknownEarly.Id, PushB, T0.AddHours(-2), SavedRequestStatus.Unknown),
                new OtherPush(posting.Id, null, T0.AddMinutes(2), SavedRequestStatus.Posting),
                new OtherPush(partial.Id, null, T0.AddMinutes(3), SavedRequestStatus.Partial),
                new OtherPush(pendingLater.Id, PushA, T0.AddHours(3), SavedRequestStatus.Pending),
            ],
            others);
    }

    [ContractFact]
    public async Task Should_ListNoOtherPushes_When_ReferenceEmpty()
    {
        var store = CreateStore();
        var self = await CreateAsync(store, NewRequest(Body("")));
        await CreateAsync(store, NewRequest(Body("")));

        Assert.Empty(await V2(store).OtherUnpostedPushesAsync(self.Id, default));
        Assert.Empty(await V2(store).OtherUnpostedPushesAsync(self.Id + 1000, default));
    }

    // ---- historyCount (8.4) ----------------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_CountHistory_When_EventsAndChanges()
    {
        var store = CreateStore();
        var other = await CreateAsync(store, NewRequest());
        var created = await CreateAsync(store, NewRequest(Body(rows: 1)));
        var body2 = Body(rows: 2, controlTotal: "20.00");
        var payload2 = JsonDocument.Parse(body2).RootElement.Clone();
        var edited = (await store.AppendVersionAsync(
            created.Id, created.RowVersion,
            new NewVersion(
                payload2, SavedPayloadReader.Read(payload2), PayloadCanonicalizer.Sha256(payload2, PayloadRules.V2),
                new HashSet<string>(),
                PayloadDiff.Compute(JsonDocument.Parse(Body(rows: 1)).RootElement, payload2, PayloadRules.V2), "add a row",
                Snapshot(version: 2), new DuplicateMatch(null, []), Admin, T0.AddMinutes(1)),
            default)).Record!;
        await store.SetValidationAsync(
            created.Id, Snapshot(version: 2), Snapshot(version: 2), new DuplicateMatch(null, []), Admin, T0.AddMinutes(2), default);
        Assert.Equal(2, edited.CurrentVersion);

        var expected = await Feed(store).AuditEventCountAsync(
            new AuditQuery(null, null, null, created.Id, null, null, [], null, false, 1, 50), default);
        var read = (await V2(store).GetV2Async(created.Id, default))!;
        var rows = (await V2(store).ListV2Async(Query(), default)).Items;

        // received, two changes (row added, control total), validated.
        Assert.Equal(4, expected);
        Assert.Equal(expected, read.HistoryCount);
        Assert.Equal(expected, rows.Single(r => r.Id == created.Id).HistoryCount);
        Assert.Equal(1, rows.Single(r => r.Id == other.Id).HistoryCount);
    }

    // ---- list row fields (9.1) -------------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_FillV2Fields_When_Listed()
    {
        var store = CreateStore();
        var metadata = new SaveMetadata(
            "Subject", T0.AddHours(-1), "ops@example.com", "<m1@example.com>", PushA, [SaveMetadataReader.PushIdError]);
        var v2 = Snapshot(ValidationState.Held, findings: 2);
        var created = await CreateAsync(
            store,
            NewRequest(
                """{"reference":"R1","controlTotal":20.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","post":false},{"externalId":"e2","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme"}]}""",
                metadata: metadata, snapshotV2: v2));

        var row = Assert.Single((await V2(store).ListV2Async(Query(), default)).Items);

        Assert.Equal(created.Id, row.Id);
        Assert.Equal(metadata.EmailSubject, row.Metadata.EmailSubject);
        Assert.Equal(metadata.EmailReceivedUtc, row.Metadata.EmailReceivedUtc);
        Assert.Equal(metadata.EmailFrom, row.Metadata.EmailFrom);
        Assert.Equal(metadata.EmailMessageId, row.Metadata.EmailMessageId);
        Assert.Equal(PushA, row.Metadata.PushId);
        Assert.Equal(metadata.MetadataErrors, row.Metadata.MetadataErrors);
        Assert.Equal(1, row.SkipCount);
        Assert.Equal(ValidationState.Held, row.ValidationV2State);
        Assert.Equal(T0, row.ValidationV2Utc);
        Assert.Equal(1, row.ValidationV2Version);
        Assert.True(row.NeedsAttentionV2);
        Assert.Equal(2, row.ProblemCount);
        Assert.Equal(ValidationState.Ok, row.ValidationState);
    }
}
