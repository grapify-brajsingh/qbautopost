using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Dashboard;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1211 (dashboard-api-m12 4.2): the facts the summary is built from, pinned on every store. Event facts equal the
/// audit counts; posted facts are the posted and partial attempts by their <c>post-finished</c> time; post-time facts
/// use the <see cref="PostSummaries.Of"/> definition.
/// </summary>
public abstract class SummaryStoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Client = "api-client:n8n";
    private const string Admin = "user-7";

    private static readonly string[] SummaryActions =
        [AuditActions.Received, AuditActions.Posted, AuditActions.PostFailed, AuditActions.Discarded];

    protected abstract ISavedRequestStore CreateStore();

    private static IDashboardSummaryStore Summary(ISavedRequestStore store) => Assert.IsAssignableFrom<IDashboardSummaryStore>(store);

    private static ISavedRequestAuditStore Feed(ISavedRequestStore store) => Assert.IsAssignableFrom<ISavedRequestAuditStore>(store);

    private static NewSavedRequest NewRequest(string reference, DateTime at, DateTime? emailReceivedUtc = null)
    {
        var body = $$"""{"reference":"{{reference}}","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        return new NewSavedRequest(
            Client, Client, body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, new ValidationSnapshot(ValidationState.Ok, at, 1, null, [], [], null),
            new DuplicateMatch(null, []), at)
        {
            Metadata = SaveMetadata.None with { EmailReceivedUtc = emailReceivedUtc },
        };
    }

    private static async Task<long> SaveAsync(ISavedRequestStore store, string reference, DateTime at, DateTime? emailReceivedUtc = null) =>
        (await store.CreateAsync(NewRequest(reference, at, emailReceivedUtc), default)).Record.Id;

    private static JsonNode Result(params decimal[] amounts) => new JsonObject
    {
        ["postId"] = "P-1",
        ["posted"] = new JsonArray(amounts.Select((a, i) => (JsonNode)new JsonObject
        {
            ["index"] = i, ["requestId"] = $"r{i}", ["txnId"] = $"T{i}", ["kind"] = "check", ["account"] = "Checking 1234",
            ["amount"] = a,
        }).ToArray()),
    };

    private static async Task<long> AttemptAsync(
        ISavedRequestStore store, long id, AttemptOutcome outcome, SavedRequestStatus next, DateTime finished, JsonNode? result = null)
    {
        var claim = await store.TryClaimAsync(id, null, Client, false, finished.AddMinutes(-1), default);
        var ok = outcome is AttemptOutcome.Posted or AttemptOutcome.Partial;
        await store.CompleteAttemptAsync(
            claim.AttemptId!.Value,
            new AttemptCompletion(outcome, next, ok ? 200 : 500, ok ? "P-1" : null, result, null, ok ? null : "boom", finished),
            default);
        return claim.AttemptId!.Value;
    }

    private static AuditQuery Count(string action, DateTime from, DateTime to) =>
        new(from, to, null, null, null, null, [action], null, IncludeUserEvents: false, 1, 1);

    [ContractFact]
    public async Task Should_ReturnEventFactsMatchingAuditCounts_When_RangeGiven()
    {
        var store = CreateStore();
        var s1 = await SaveAsync(store, "S1", T0);
        var s2 = await SaveAsync(store, "S2", T0.AddMinutes(10));
        var s3 = await SaveAsync(store, "S3", T0.AddMinutes(20));
        var s4 = await SaveAsync(store, "S4", T0.AddMinutes(30));
        await AttemptAsync(store, s1, AttemptOutcome.Held, SavedRequestStatus.Pending, T0.AddMinutes(2));
        await AttemptAsync(store, s1, AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddMinutes(4), Result(1m));
        await AttemptAsync(store, s2, AttemptOutcome.Partial, SavedRequestStatus.Partial, T0.AddMinutes(12), Result(2m));
        await AttemptAsync(store, s3, AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddMinutes(25), Result(3m));
        await store.ChangeStatusAsync(
            s4, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Admin, T0.AddMinutes(31),
            SavedRequestEvents.Discarded, new JsonObject { ["reason"] = "Sent Twice" }, default);

        foreach (var (from, to) in new[] { (T0, T0.AddHours(1)), (T0.AddMinutes(4), T0.AddMinutes(25)), (T0.AddMinutes(1), T0.AddMinutes(31)) })
        {
            var facts = await Summary(store).EventFactsAsync(from, to, default);

            Assert.All(facts, f => Assert.True(f.AtUtc >= from && f.AtUtc < to));
            Assert.All(facts, f => Assert.Contains(f.Action, SummaryActions));
            foreach (var action in SummaryActions)
            {
                Assert.Equal(
                    await Feed(store).AuditEventCountAsync(Count(action, from, to), default),
                    facts.Count(f => f.Action == action));
            }
        }

        var all = await Summary(store).EventFactsAsync(T0, T0.AddHours(1), default);
        Assert.Equal(4, all.Count(f => f.Action == AuditActions.Received));
        Assert.Equal(2, all.Count(f => f.Action == AuditActions.Posted));
        Assert.Equal(2, all.Count(f => f.Action == AuditActions.PostFailed));
        Assert.Equal(1, all.Count(f => f.Action == AuditActions.Discarded));
    }

    [ContractFact]
    public async Task Should_ReturnPostedAndPartialFacts_When_InRange()
    {
        var store = CreateStore();
        var s1 = await SaveAsync(store, "S1", T0);
        var s2 = await SaveAsync(store, "S2", T0);
        var s3 = await SaveAsync(store, "S3", T0);
        var s4 = await SaveAsync(store, "S4", T0);
        await AttemptAsync(store, s1, AttemptOutcome.Held, SavedRequestStatus.Pending, T0.AddMinutes(2));
        var posted = await AttemptAsync(store, s1, AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddMinutes(4), Result(10.00m, 0.25m));
        var partial = await AttemptAsync(store, s2, AttemptOutcome.Partial, SavedRequestStatus.Partial, T0.AddMinutes(12), Result(7.10m));
        await AttemptAsync(store, s3, AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddMinutes(30), Result(99m)); // at `to`: excluded
        await AttemptAsync(store, s4, AttemptOutcome.Unknown, SavedRequestStatus.Unknown, T0.AddMinutes(14));
        await store.ResolveAsync(s4, ResolveOutcome.Posted, "checked QuickBooks", Admin, T0.AddMinutes(15), default);
        await store.ResolveAsync(s2, ResolveOutcome.Posted, "rest posted by hand", Admin, T0.AddMinutes(16), default);

        var facts = (await Summary(store).PostedFactsAsync(T0.AddMinutes(4), T0.AddMinutes(30), default))
            .OrderBy(f => f.AttemptId).ToList();

        Assert.Equal(new[] { posted, partial }, facts.Select(f => f.AttemptId));
        Assert.Equal(new[] { T0.AddMinutes(4), T0.AddMinutes(12) }, facts.Select(f => f.FinishedUtc));
        Assert.Equal((2, 10.25m), AttemptResultRows.Read(facts[0].ResultJson));
        Assert.Equal((1, 7.10m), AttemptResultRows.Read(facts[1].ResultJson));
        Assert.Empty(await Summary(store).PostedFactsAsync(T0.AddMinutes(5), T0.AddMinutes(12), default));
    }

    [ContractFact]
    public async Task Should_UseResolvedTime_When_RequestResolvedToPosted()
    {
        var store = CreateStore();
        var received = T0.AddHours(-5);
        var s1 = await SaveAsync(store, "S1", T0, emailReceivedUtc: received);
        var s2 = await SaveAsync(store, "S2", T0);
        var s3 = await SaveAsync(store, "S3", T0);
        await AttemptAsync(store, s1, AttemptOutcome.Unknown, SavedRequestStatus.Unknown, T0.AddMinutes(10));
        await store.ResolveAsync(s1, ResolveOutcome.Posted, "checked QuickBooks", Admin, T0.AddMinutes(40), default);
        await AttemptAsync(store, s2, AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddMinutes(20), Result(1m));
        await AttemptAsync(store, s3, AttemptOutcome.Unknown, SavedRequestStatus.Unknown, T0.AddMinutes(25));
        await store.ResolveAsync(s3, ResolveOutcome.NotPosted, "nothing in QuickBooks", Admin, T0.AddMinutes(26), default);

        var facts = (await Summary(store).PostTimeFactsAsync(T0, T0.AddHours(1), default)).OrderBy(f => f.SavedRequestId).ToList();

        Assert.Equal(
            new[] { new PostTimeFact(s1, received, T0.AddMinutes(40)), new PostTimeFact(s2, T0, T0.AddMinutes(20)) },
            facts);
        Assert.Equal(
            [new PostTimeFact(s2, T0, T0.AddMinutes(20))],
            await Summary(store).PostTimeFactsAsync(T0, T0.AddMinutes(40), default));
    }
}
