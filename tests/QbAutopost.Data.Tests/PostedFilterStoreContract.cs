using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Dashboard;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1211 (dashboard-api-m12 5): the posted-time filters and sort of the v2 list, and the instant bounds of the received
/// and created filters, on every store. The posted time is the <see cref="PostSummaries.Of"/> one, pinned against the
/// <c>postSummary</c> the list itself returns.
/// </summary>
public abstract class PostedFilterStoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private const string Client = "api-client:n8n";
    private const string Admin = "user-7";

    protected abstract ISavedRequestStore CreateStore();

    private static ISavedRequestV2Store V2(ISavedRequestStore store) => Assert.IsAssignableFrom<ISavedRequestV2Store>(store);

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

    private static async Task AttemptAsync(
        ISavedRequestStore store, long id, AttemptOutcome outcome, SavedRequestStatus next, DateTime finished)
    {
        var claim = await store.TryClaimAsync(id, null, Client, false, finished.AddMinutes(-1), default);
        var ok = outcome is AttemptOutcome.Posted or AttemptOutcome.Partial;
        await store.CompleteAttemptAsync(
            claim.AttemptId!.Value,
            new AttemptCompletion(outcome, next, ok ? 200 : 500, ok ? "P-1" : null, new JsonObject { ["posted"] = new JsonArray() },
                null, ok ? null : "boom", finished),
            default);
    }

    private static async Task<long> PostedAtAsync(ISavedRequestStore store, string reference, DateTime finished)
    {
        var id = await SaveAsync(store, reference, T0.AddDays(-3));
        await AttemptAsync(store, id, AttemptOutcome.Posted, SavedRequestStatus.Posted, finished);
        return id;
    }

    private static SavedRequestQuery Base(string sort = "-created", DateOnly? createdFrom = null, DateOnly? createdTo = null) =>
        new([], null, null, null, createdFrom, createdTo, null, sort, 1, 200);

    private static SavedRequestQueryV2 Query(
        SavedRequestQuery? @base = null, DateOnly? receivedFrom = null, DateOnly? receivedTo = null) =>
        new(@base ?? Base(), receivedFrom, receivedTo, null, null, null, null, null);

    private static async Task<long[]> IdsAsync(ISavedRequestStore store, SavedRequestQueryV2 query) =>
        (await V2(store).ListV2Async(query, default)).Items.Select(i => i.Id).ToArray();

    // This project runs in invariant globalization (no ICU on Windows), where only the Windows id resolves.
    private static TimeZoneInfo Kolkata() =>
        DateBounds.TryZone("Asia/Kolkata", out var tz) ? tz! : TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    private static DateTime Bound(string raw, TimeZoneInfo? tz, bool upper)
    {
        Assert.True(DateBounds.TryParse(raw, tz, upper, out var utc));
        return utc;
    }

    [ContractFact]
    public async Task Should_FilterByPostedLocalDay_When_TzGiven()
    {
        var store = CreateStore();
        var before = await PostedAtAsync(store, "B", new DateTime(2026, 10, 26, 18, 29, 59, DateTimeKind.Utc));
        var first = await PostedAtAsync(store, "F", new DateTime(2026, 10, 26, 18, 30, 0, DateTimeKind.Utc));
        var last = await PostedAtAsync(store, "L", new DateTime(2026, 10, 27, 18, 29, 59, DateTimeKind.Utc));
        var after = await PostedAtAsync(store, "A", new DateTime(2026, 10, 27, 18, 30, 0, DateTimeKind.Utc));
        var unposted = await SaveAsync(store, "U", T0);
        var kolkata = Kolkata();

        var ids = await IdsAsync(store, Query() with
        {
            PostedFromUtc = Bound("2026-10-27", kolkata, upper: false),
            PostedToUtc = Bound("2026-10-27", kolkata, upper: true),
        });

        Assert.Equal(new[] { first, last }.Order(), ids.Order());
        Assert.DoesNotContain(before, ids);
        Assert.DoesNotContain(after, ids);
        Assert.DoesNotContain(unposted, ids);
        Assert.Equal(new[] { first, last, after }.Order(),
            (await IdsAsync(store, Query() with { PostedFromUtc = Bound("2026-10-27", kolkata, upper: false) })).Order());
        Assert.Equal(new[] { before, first }.Order(),
            (await IdsAsync(store, Query() with { PostedToUtc = Bound("2026-10-26T18:30:01Z", null, upper: true) })).Order());
    }

    [ContractTheory]
    [InlineData("posted")]
    [InlineData("-posted")]
    public async Task Should_SortUnpostedLast_When_SortPosted(string sort)
    {
        var store = CreateStore();
        var unpostedA = await SaveAsync(store, "UA", T0);
        var late = await PostedAtAsync(store, "LATE", T0.AddHours(5));
        var early = await PostedAtAsync(store, "EARLY", T0.AddHours(1));
        var unpostedB = await SaveAsync(store, "UB", T0);
        var tieA = await PostedAtAsync(store, "TA", T0.AddHours(3));
        var tieB = await PostedAtAsync(store, "TB", T0.AddHours(3));

        var ids = await IdsAsync(store, Query(Base(sort)));

        long[] posted = sort == "posted" ? [early, tieB, tieA, late] : [late, tieB, tieA, early];
        Assert.Equal([.. posted, unpostedB, unpostedA], ids);
    }

    [ContractFact]
    public async Task Should_MatchPostSummary_When_FilteringByPosted()
    {
        var store = CreateStore();
        await PostedAtAsync(store, "P1", T0.AddHours(1));
        var heldThenPosted = await SaveAsync(store, "P2", T0);
        await AttemptAsync(store, heldThenPosted, AttemptOutcome.Held, SavedRequestStatus.Pending, T0.AddHours(2));
        await AttemptAsync(store, heldThenPosted, AttemptOutcome.Posted, SavedRequestStatus.Posted, T0.AddHours(6));
        var partial = await SaveAsync(store, "P3", T0);
        await AttemptAsync(store, partial, AttemptOutcome.Partial, SavedRequestStatus.Partial, T0.AddHours(3));
        var partialResolved = await SaveAsync(store, "P4", T0);
        await AttemptAsync(store, partialResolved, AttemptOutcome.Partial, SavedRequestStatus.Partial, T0.AddHours(4));
        await store.ResolveAsync(partialResolved, ResolveOutcome.Posted, "rest by hand", Admin, T0.AddHours(9), default);
        var unknownResolved = await SaveAsync(store, "P5", T0);
        await AttemptAsync(store, unknownResolved, AttemptOutcome.Unknown, SavedRequestStatus.Unknown, T0.AddHours(1));
        await store.ResolveAsync(unknownResolved, ResolveOutcome.Posted, "checked", Admin, T0.AddHours(5), default);
        var unknownNotPosted = await SaveAsync(store, "P6", T0);
        await AttemptAsync(store, unknownNotPosted, AttemptOutcome.Unknown, SavedRequestStatus.Unknown, T0.AddHours(2));
        await store.ResolveAsync(unknownNotPosted, ResolveOutcome.NotPosted, "nothing", Admin, T0.AddHours(3), default);
        var failed = await SaveAsync(store, "P7", T0);
        await AttemptAsync(store, failed, AttemptOutcome.Failed, SavedRequestStatus.Pending, T0.AddHours(4));
        await SaveAsync(store, "P8", T0);

        var every = (await V2(store).ListV2Async(Query(), default)).Items;
        Assert.Equal(5, every.Count(i => i.PostSummary?.PostedUtc is not null));
        var windows = new (DateTime? From, DateTime? To)[]
        {
            (T0, T0.AddDays(1)), (T0.AddHours(3), T0.AddHours(5)), (T0.AddHours(4), null), (null, T0.AddHours(4)),
            (T0.AddHours(5), T0.AddHours(6)), (T0.AddHours(9), T0.AddHours(9).AddTicks(10)),
        };
        foreach (var (from, to) in windows)
        {
            var expected = every
                .Where(i => i.PostSummary?.PostedUtc is { } at && (from is null || at >= from) && (to is null || at < to))
                .Select(i => i.Id).Order().ToArray();

            var ids = await IdsAsync(store, Query() with { PostedFromUtc = from, PostedToUtc = to });

            Assert.Equal(expected, ids.Order().ToArray());
        }

        var sorted = await IdsAsync(store, Query(Base("posted")));
        Assert.Equal(
            every.Where(i => i.PostSummary?.PostedUtc is not null)
                .OrderBy(i => i.PostSummary!.PostedUtc).ThenByDescending(i => i.Id).Select(i => i.Id),
            sorted.Take(5));
    }

    [ContractFact]
    public async Task Should_BehaveAsBefore_When_OnlyDateOnlyFiltersSet()
    {
        var store = CreateStore();
        var day1 = await SaveAsync(store, "D1", new DateTime(2026, 10, 25, 23, 59, 59, DateTimeKind.Utc));
        var day2 = await SaveAsync(store, "D2", new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
        var day2Late = await SaveAsync(store, "D3", new DateTime(2026, 10, 26, 23, 59, 59, DateTimeKind.Utc));
        var day3 = await SaveAsync(store, "D4", new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc));
        var emailed = await SaveAsync(store, "D5", new DateTime(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc),
            emailReceivedUtc: new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc));
        var day = new DateOnly(2026, 10, 26);

        Assert.Equal(new[] { day2, day2Late }.Order(),
            (await IdsAsync(store, Query(Base(createdFrom: day, createdTo: day)))).Order());
        Assert.Equal(new[] { day2, day2Late, emailed }.Order(),
            (await IdsAsync(store, Query(receivedFrom: day, receivedTo: day))).Order());
        Assert.Equal(new[] { emailed, day3, day2Late, day2, day1 }, await IdsAsync(store, Query()));

        // The instant bounds sit beside them: from inclusive, to exclusive.
        var at = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new[] { day2 },
            await IdsAsync(store, Query() with { CreatedFromUtc = at, CreatedToUtc = at.AddSeconds(1) }));
        Assert.Equal(new[] { emailed, day2 }.Order(),
            (await IdsAsync(store, Query() with { ReceivedFromUtc = at, ReceivedToUtc = at.AddHours(6).AddTicks(10) })).Order());
        Assert.Equal(new[] { day2 },
            await IdsAsync(store, Query() with { ReceivedFromUtc = at, ReceivedToUtc = at.AddHours(6) }));
    }
}
