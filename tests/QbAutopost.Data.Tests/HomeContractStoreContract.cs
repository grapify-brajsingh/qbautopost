using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1204 (dashboard-api-m12 3.1): the audit-feed behaviour the Home page counts on, pinned on every store. Data set:
/// 4 saves (a partial request is locked and only a pending one can be discarded, so 3 do not suffice); 2 posted attempts, 1 failed (outcome held) and 1 partial; discard, restore, discard; 3 user-audit rows.
/// </summary>
public abstract class HomeContractStoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Client = "api-client:n8n";
    private const string Admin = "user-7";

    protected abstract ISavedRequestStore CreateStore();

    protected abstract Task<UserAuditEntry> AddUserAuditAsync(ISavedRequestStore store, UserAuditEntry entry);

    private static ISavedRequestAuditStore Feed(ISavedRequestStore store) => Assert.IsAssignableFrom<ISavedRequestAuditStore>(store);

    private static NewSavedRequest NewRequest(string reference, DateTime at)
    {
        var body = $$"""{"reference":"{{reference}}","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        return new NewSavedRequest(
            Client, Client, body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, new ValidationSnapshot(ValidationState.Ok, at, 1, null, [], [], null),
            new DuplicateMatch(null, []), at);
    }

    private static AuditQuery Query(
        bool includeUserEvents = true, int page = 1, int pageSize = 200, IReadOnlyList<string>? actions = null,
        DateTime? from = null, DateTime? to = null) =>
        new(from, to, null, null, null, null, actions ?? [], null, includeUserEvents, page, pageSize);

    private static async Task<long> SaveAsync(ISavedRequestStore store, string reference, DateTime at) =>
        (await store.CreateAsync(NewRequest(reference, at), default)).Record.Id;

    private static async Task AttemptAsync(
        ISavedRequestStore store, long id, AttemptOutcome outcome, SavedRequestStatus next, DateTime started, DateTime finished)
    {
        var claim = await store.TryClaimAsync(id, null, Client, false, started, default);
        var posted = outcome == AttemptOutcome.Posted;
        await store.CompleteAttemptAsync(
            claim.AttemptId!.Value,
            new AttemptCompletion(outcome, next, posted ? 200 : 500, posted ? "P-1" : null, null, null, posted ? null : "boom", finished),
            default);
    }

    private sealed record Seeded(long S1, long S2, long S3, long S4, DateTime Saved1, DateTime Saved2, DateTime Saved3, DateTime Posted1, DateTime Posted2);

    private static async Task<Seeded> SeedCoreAsync(ISavedRequestStore store)
    {
        var (t1, t2, t3, t4) = (T0, T0.AddMinutes(10), T0.AddMinutes(16), T0.AddMinutes(20));
        var s1 = await SaveAsync(store, "H1", t1);
        var s2 = await SaveAsync(store, "H2", t2);
        var s3 = await SaveAsync(store, "H3", t3);
        var s4 = await SaveAsync(store, "H4", t4);

        await AttemptAsync(store, s1, AttemptOutcome.Held, SavedRequestStatus.Pending, T0.AddMinutes(1), T0.AddMinutes(2));
        var posted1 = T0.AddMinutes(4);
        await AttemptAsync(store, s1, AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddMinutes(3), posted1);

        await AttemptAsync(store, s2, AttemptOutcome.Partial, SavedRequestStatus.Partial, T0.AddMinutes(11), T0.AddMinutes(12));
        var posted2 = T0.AddMinutes(18);
        await AttemptAsync(store, s3, AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddMinutes(17), posted2);

        await store.ChangeStatusAsync(
            s4, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Admin, T0.AddMinutes(21),
            SavedRequestEvents.Discarded, new JsonObject { ["reason"] = "Sent Twice" }, default);
        await store.ChangeStatusAsync(
            s4, null, SavedRequestStatus.Discarded, SavedRequestStatus.Pending, Admin, T0.AddMinutes(22),
            SavedRequestEvents.Restored, null, default);
        await store.ChangeStatusAsync(
            s4, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Admin, T0.AddMinutes(23),
            SavedRequestEvents.Discarded, new JsonObject { ["reason"] = "Sent Twice" }, default);

        return new Seeded(s1, s2, s3, s4, t1, t2, t3, posted1, posted2);
    }

    private async Task<Seeded> SeedAsync(ISavedRequestStore store)
    {
        var seeded = await SeedCoreAsync(store);
        foreach (var kind in new[] { "created", "login", "role-changed" })
        {
            await AddUserAuditAsync(store, new UserAuditEntry(0, Admin, Admin, kind, null, T0.AddMinutes(30)));
        }

        return seeded;
    }

    private static async Task<List<AuditEvent>> AllAsync(ISavedRequestStore store, AuditQuery query)
    {
        // Pages until an empty one, so a total that understates the rows cannot end the walk early.
        var all = new List<AuditEvent>();
        for (var page = 1; page <= 500; page++)
        {
            var result = await Feed(store).AuditEventsAsync(query with { Page = page }, default);
            if (result.Items.Count == 0)
            {
                return all;
            }

            all.AddRange(result.Items);
        }

        throw new InvalidOperationException("The feed did not end within 500 pages.");
    }

    [ContractFact]
    public async Task Should_CountAllMatches_When_PageSizeIsOne()
    {
        var store = CreateStore();
        await SeedAsync(store);

        var one = await Feed(store).AuditEventsAsync(Query(pageSize: 1), default);
        var across = await AllAsync(store, Query(pageSize: 1));

        Assert.Single(one.Items);
        Assert.True(one.Total > 1);
        Assert.Equal(one.Total, across.Count);
        Assert.Equal(one.Total, await Feed(store).AuditEventCountAsync(Query(pageSize: 1), default));
        var known = new (string Action, int Count)[]
        {
            (AuditActions.Received, 4), (AuditActions.Posted, 2), (AuditActions.PostFailed, 2), (AuditActions.Discarded, 2),
        };
        foreach (var (action, count) in known)
        {
            var q = Query(pageSize: 1, actions: [action]);
            Assert.Equal(count, (await AllAsync(store, q)).Count);
            Assert.Equal(count, (await Feed(store).AuditEventsAsync(q, default)).Total);
        }
    }

    [ContractFact]
    public async Task Should_MatchAnyAction_When_ActionRepeated()
    {
        var store = CreateStore();
        await SeedAsync(store);

        var feed = await Feed(store).AuditEventsAsync(Query(actions: [AuditActions.Posted, AuditActions.Discarded]), default);

        Assert.Equal(4, feed.Total);
        Assert.Equal(2, feed.Items.Count(e => e.Action == AuditActions.Posted));
        Assert.Equal(2, feed.Items.Count(e => e.Action == AuditActions.Discarded));
    }

    [ContractFact]
    public async Task Should_EmitOneReceivedPerSave_When_Saved()
    {
        var store = CreateStore();
        var s = await SeedAsync(store);

        var received = (await Feed(store).AuditEventsAsync(Query(actions: [AuditActions.Received]), default)).Items;

        Assert.Equal(4, received.Count);
        Assert.Equal(new[] { s.S1, s.S2, s.S3, s.S4 }.Order(), received.Select(e => e.RequestId!.Value).Order());
        Assert.Equal(new[] { s.Saved1, s.Saved2, s.Saved3, T0.AddMinutes(20) }.Order(), received.Select(e => e.AtUtc).Order());
    }

    [ContractFact]
    public async Task Should_EmitOnePostedPerPostedAttempt_When_Finished()
    {
        var store = CreateStore();
        var s = await SeedAsync(store);

        var posted = (await Feed(store).AuditEventsAsync(Query(actions: [AuditActions.Posted]), default)).Items;

        Assert.Equal(2, posted.Count);
        Assert.Equal(new[] { s.Posted1, s.Posted2 }.Order(), posted.Select(e => e.AtUtc).Order());
    }

    [ContractFact]
    public async Task Should_CountPartialAsPostFailed_When_AttemptPartial()
    {
        var store = CreateStore();
        await SeedAsync(store);

        var failed = (await Feed(store).AuditEventsAsync(Query(actions: [AuditActions.PostFailed]), default)).Items;

        Assert.Equal(2, failed.Count);
        Assert.Equal(new[] { "held", "partial" }, failed.Select(e => (string)e.Detail!["outcome"]!).Order());
        Assert.Equal(new[] { T0.AddMinutes(2), T0.AddMinutes(12) }, failed.Select(e => e.AtUtc).Order());
    }

    [ContractFact]
    public async Task Should_EmitTwoDiscarded_When_DiscardRestoreDiscard()
    {
        var store = CreateStore();
        var s = await SeedAsync(store);

        var discarded = (await Feed(store).AuditEventsAsync(Query(actions: [AuditActions.Discarded]), default)).Items;
        var restored = (await Feed(store).AuditEventsAsync(Query(actions: [AuditActions.Restored]), default)).Items;

        Assert.Equal(2, discarded.Count);
        Assert.All(discarded, e => Assert.Equal(s.S4, e.RequestId));
        Assert.Single(restored);
    }

    [ContractFact]
    public async Task Should_ExcludeUserEventsFromTotal_When_CallerLacksUsersManage()
    {
        var store = CreateStore();
        await SeedAsync(store);

        var with = await Feed(store).AuditEventsAsync(Query(includeUserEvents: true), default);
        var without = await Feed(store).AuditEventsAsync(Query(includeUserEvents: false), default);

        Assert.Equal(3, with.Total - without.Total);
        Assert.Equal(without.Total, without.Items.Count);
        Assert.DoesNotContain(without.Items, e => e.Action.StartsWith("user-", StringComparison.Ordinal));
        Assert.Contains(with.Items, e => e.Action.StartsWith("user-", StringComparison.Ordinal));
    }

    [ContractFact]
    public async Task Should_ReturnEachEventOnce_When_PagingBy200()
    {
        var store = CreateStore();
        await SeedAsync(store);
        // More than two pages, all at one instant, so the id tie-break is what keeps the pages apart.
        for (var i = 0; i < 430; i++)
        {
            await AddUserAuditAsync(store, new UserAuditEntry(0, Admin, Admin, "login", null, T0.AddMinutes(40)));
        }

        var total = (await Feed(store).AuditEventsAsync(Query(), default)).Total;
        var ids = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            ids.AddRange((await Feed(store).AuditEventsAsync(Query(page: page), default)).Items.Select(e => e.Id));
        }

        Assert.True(total > 400);
        Assert.Equal(total, ids.Count);
        Assert.Equal(total, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [ContractFact]
    public async Task Should_ExcludeEventAtTo_When_ToEqualsEventTime()
    {
        var store = CreateStore();
        var s = await SeedAsync(store);

        var upTo = await Feed(store).AuditEventsAsync(Query(actions: [AuditActions.Received], to: s.Saved2), default);
        var from = await Feed(store).AuditEventsAsync(Query(actions: [AuditActions.Received], from: s.Saved2), default);

        Assert.Equal(new[] { s.S1 }, upTo.Items.Select(e => e.RequestId!.Value));
        Assert.Equal(1, upTo.Total);
        Assert.Equal(new[] { s.S2, s.S3, s.S4 }.Order(), from.Items.Select(e => e.RequestId!.Value).Order());
    }

    [ContractFact]
    public async Task Should_SortByReceivedThenCreated_When_QueueListed()
    {
        var store = CreateStore();
        var noEmail = (await store.CreateAsync(NewRequest("Q1", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc)), default)).Record.Id;
        var withEmail = (await store.CreateAsync(
            NewRequest("Q2", new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc)) with
            {
                Metadata = SaveMetadata.None with { EmailReceivedUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc) },
            },
            default)).Record.Id;
        var middle = (await store.CreateAsync(NewRequest("Q3", new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc)), default)).Record.Id;
        var older = (await store.CreateAsync(NewRequest("Q4", new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc)), default)).Record.Id;
        await store.ChangeStatusAsync(
            older, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Admin, T0, SavedRequestEvents.Discarded,
            new JsonObject { ["reason"] = "Sent Twice" }, default);

        var statuses = new[] { SavedRequestStatus.Pending, SavedRequestStatus.Partial, SavedRequestStatus.Unknown };
        var query = new SavedRequestQueryV2(
            new SavedRequestQuery(statuses, null, null, null, null, null, null, "received", 1, 100),
            null, null, null, null, null, null, null);

        var ids = (await Assert.IsAssignableFrom<ISavedRequestV2Store>(store).ListV2Async(query, default)).Items.Select(i => i.Id);

        Assert.Equal(new[] { withEmail, middle, noEmail }, ids);
    }
}
