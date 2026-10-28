using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1106: the audit feed every <see cref="ISavedRequestStore"/> serves (spec saved-requests-dashboard-v2 8.1-8.3).
/// The EF Core store reads the view <c>audit_events</c>, the fake applies <see cref="AuditEventMapper"/>; both run
/// these tests, and <see cref="Should_MatchMapperOutput_When_ViewRead"/> holds the SQL and the C# mapping together
/// over seeded data that produces every action of the table.
/// </summary>
public abstract class AuditEventsStoreContract
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Client = "api-client:n8n";
    private const string Admin = "user-7";
    private const string ReferenceB = "REF-B-77";
    private static readonly Guid PushB = Guid.Parse("6f1c2a4e-9b7d-4c3e-8a21-0d5e6f7a8b9c");

    protected abstract ISavedRequestStore CreateStore();

    /// <summary>Writes a <c>user_audit</c> row as the app would and returns it with its id.</summary>
    protected abstract Task<UserAuditEntry> AddUserAuditAsync(ISavedRequestStore store, UserAuditEntry entry);

    // Every store serves the feed through ISavedRequestAuditStore (kept off the pinned ISavedRequestStore surface).
    private static ISavedRequestAuditStore Feed(ISavedRequestStore store) => Assert.IsAssignableFrom<ISavedRequestAuditStore>(store);

    // ---- builders ------------------------------------------------------------------------------------------

    private static string Row(string externalId, string payee, string? post = null) =>
        $$"""{"externalId":"{{externalId}}","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"{{payee}}","memo":"rent"{{(post is null ? "" : ",\"post\":" + post)}}}""";

    private static string Body(string reference, string controlTotal, string extra, params string[] rows) =>
        $$"""{"reference":"{{reference}}","controlTotal":{{controlTotal}}{{extra}},"transactions":[{{string.Join(',', rows)}}]}""";

    private static readonly string BodyA1 = Body("R1", "30.00", "", Row("e1", "Acme"), Row("e2", "Acme"), Row("e3", "Beta"));
    private static readonly string BodyA2 =
        Body("R2", "40.00", ",\"x y\":1", Row("e1", "Acme", "false"), Row("e2", "Acme Ltd"), Row("e4", "Zeta"));
    private static readonly string BodyA3 =
        Body("R2", "40.00", ",\"x y\":1", Row("e1", "Acme", "true"), Row("e2", "Acme Ltd"), Row("e4", "Zeta"));
    private static readonly string BodyB = Body(ReferenceB, "10.00", "", Row("b1", "Gamma"));

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static ValidationSnapshot Snapshot(int version, ValidationState state = ValidationState.Ok) =>
        new(state, T0, version, null, [], [], null);

    private static NewSavedRequest NewRequest(string body, DuplicateMatch duplicate, SaveMetadata metadata)
    {
        var payload = Json(body);
        return new NewSavedRequest(
            Client, Client, body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, Snapshot(1), duplicate, T0)
        {
            Metadata = metadata,
        };
    }

    private static NewVersion NewVersionOf(string previous, string body, string? note, int number, DateTime at)
    {
        var payload = Json(body);
        return new NewVersion(
            payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload, PayloadRules.V2),
            new HashSet<string>(), PayloadDiff.Compute(Json(previous), payload, PayloadRules.V2), note,
            Snapshot(number), new DuplicateMatch(null, []), Admin, at);
    }

    private static AuditQuery Query(
        bool includeUserEvents = true, int page = 1, int pageSize = 200, string? actor = null, IReadOnlyList<string>? actions = null,
        string? q = null, DateTime? from = null, DateTime? to = null, long? requestId = null, string? reference = null,
        Guid? pushId = null) =>
        new(from, to, actor, requestId, reference, pushId, actions ?? [], q, includeUserEvents, page, pageSize);

    private sealed record Seeded(long A, long B, IReadOnlyList<UserAuditEntry> Users);

    /// <summary>Two requests and a set of user rows that between them produce every action of the 8.3 table.</summary>
    private async Task<Seeded> SeedEverythingAsync(ISavedRequestStore store)
    {
        var b = (await store.CreateAsync(
            NewRequest(BodyB, new DuplicateMatch(null, []), SaveMetadata.None with { PushId = PushB }), default)).Record;
        var a = (await store.CreateAsync(
            NewRequest(BodyA1, new DuplicateMatch(b.Id, ["same-reference"]), SaveMetadata.None), default)).Record;

        // A: two edits (rows added, deleted, post off and on, edited; control total; reference; a bracketed key),
        // validated, posted.
        var v2 = (await store.AppendVersionAsync(a.Id, a.RowVersion, NewVersionOf(BodyA1, BodyA2, "fix payee", 2, T0.AddMinutes(1)), default)).Record!;
        var v3 = (await store.AppendVersionAsync(a.Id, v2.RowVersion, NewVersionOf(BodyA2, BodyA3, null, 3, T0.AddMinutes(2)), default)).Record!;
        await store.SetValidationAsync(
            a.Id, Snapshot(3, ValidationState.Held), Snapshot(3, ValidationState.Held), new DuplicateMatch(null, []), Admin,
            T0.AddMinutes(3), default);
        var claimA = await store.TryClaimAsync(a.Id, null, Admin, false, T0.AddMinutes(4), default);
        await store.CompleteAttemptAsync(
            claimA.AttemptId!.Value,
            new AttemptCompletion(AttemptOutcome.Posted, SavedRequestStatus.Posted, 200, "P-1", null, null, null, T0.AddMinutes(5)),
            default);
        Assert.Equal(3, v3.CurrentVersion);

        // B: discarded, restored, a failed post, a post lost to a restart, resolved as not posted.
        await store.ChangeStatusAsync(
            b.Id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Admin, T0.AddMinutes(6),
            SavedRequestEvents.Discarded, new JsonObject { ["reason"] = "Sent Twice" }, default);
        await store.ChangeStatusAsync(
            b.Id, null, SavedRequestStatus.Discarded, SavedRequestStatus.Pending, Admin, T0.AddMinutes(7),
            SavedRequestEvents.Restored, null, default);
        var claimB = await store.TryClaimAsync(b.Id, null, Client, false, T0.AddMinutes(8), default);
        await store.CompleteAttemptAsync(
            claimB.AttemptId!.Value,
            new AttemptCompletion(AttemptOutcome.Failed, SavedRequestStatus.Pending, 500, null, null, null, "boom", T0.AddMinutes(8)),
            default);
        await store.TryClaimAsync(b.Id, null, Client, false, T0.AddMinutes(9), default);
        await store.RecoverInFlightAsync(T0.AddMinutes(10), default);
        await store.ResolveAsync(b.Id, ResolveOutcome.NotPosted, "Checked In QuickBooks", Admin, T0.AddMinutes(11), default);

        var users = new List<UserAuditEntry>();
        var at = T0.AddMinutes(12);
        foreach (var kind in new[]
                 {
                     "created", "role-changed", "disabled", "enabled", "password-reset", "unlocked", "password-changed",
                     "login", "refresh-reuse", "mfa-enrolled",
                 })
        {
            users.Add(await AddUserAuditAsync(store, new UserAuditEntry(
                0, Admin, Admin, kind, JsonNode.Parse("""{"ip":"10.0.0.1","familyId":"f-1"}"""), at)));
        }

        users.Add(await AddUserAuditAsync(store, new UserAuditEntry(
            0, null, "anonymous", "login-failed", JsonNode.Parse("""{"email":"nobody@example.com"}"""), at)));
        users.Add(await AddUserAuditAsync(store, new UserAuditEntry(0, Admin, "anonymous", "locked-out", null, at)));
        return new Seeded(a.Id, b.Id, users);
    }

    private static async Task<List<AuditEvent>> MapperOutputAsync(ISavedRequestStore store, Seeded seeded)
    {
        var expected = new List<AuditEvent>();
        foreach (var id in new[] { seeded.A, seeded.B })
        {
            expected.AddRange((await store.EventsAsync(id, default)).Select(AuditEventMapper.FromEvent).OfType<AuditEvent>());
            var versions = await store.VersionsAsync(id, default);
            for (var i = 1; i < versions.Count; i++)
            {
                expected.AddRange(AuditEventMapper.FromVersion(versions[i], versions[i - 1], versions[i].Note));
            }
        }

        expected.AddRange(seeded.Users.Select(AuditEventMapper.FromUserAudit));
        return expected.OrderByDescending(e => e.AtUtc).ThenByDescending(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every field of an event as one comparable line; JSON with object keys sorted, so key order is no difference.</summary>
    private static string Describe(AuditEvent e) =>
        string.Join(
            " | ",
            e.Id, e.AtUtc.ToString("O"), e.ActorId, e.ActorKind, e.Action, e.RequestId, e.RowIndex, e.RowExternalId, e.Field,
            Canonical(e.Old), Canonical(e.New), e.VersionNumber, Canonical(e.Detail));

    private static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node);
        return sb.ToString();

        void Write(JsonNode? n)
        {
            switch (n)
            {
                case null:
                    sb.Append("null");
                    break;
                case JsonObject o:
                    sb.Append('{');
                    foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        sb.Append(JsonSerializer.Serialize(key)).Append(':');
                        Write(value);
                        sb.Append(',');
                    }

                    sb.Append('}');
                    break;
                case JsonArray a:
                    sb.Append('[');
                    foreach (var item in a)
                    {
                        Write(item);
                        sb.Append(',');
                    }

                    sb.Append(']');
                    break;
                default:
                    sb.Append(n.ToJsonString());
                    break;
            }
        }
    }

    private static IEnumerable<string> Expected(IEnumerable<AuditEvent> all) =>
        all.OrderByDescending(e => e.AtUtc).ThenByDescending(e => e.Id, StringComparer.Ordinal).Select(e => e.Id);

    // ---- tests ---------------------------------------------------------------------------------------------

    [ContractFact]
    public async Task Should_MatchMapperOutput_When_ViewRead()
    {
        var store = CreateStore();
        var seeded = await SeedEverythingAsync(store);

        var feed = await Feed(store).AuditEventsAsync(Query(), default);
        var expected = await MapperOutputAsync(store, seeded);

        Assert.Equal(AuditActions.All.Order(StringComparer.Ordinal), feed.Items.Select(e => e.Action).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(expected.Count, feed.Total);
        Assert.Equal(expected.Select(Describe), feed.Items.Select(Describe));
    }

    [ContractFact]
    public async Task Should_OrderNewestFirst_When_Listed()
    {
        var store = CreateStore();
        for (var i = 0; i < 11; i++)
        {
            await AddUserAuditAsync(store, new UserAuditEntry(0, Admin, Admin, "login", null, T0));
        }

        await store.CreateAsync(NewRequest(BodyB, new DuplicateMatch(null, []), SaveMetadata.None) with { NowUtc = T0.AddMinutes(1) }, default);

        var ids = (await Feed(store).AuditEventsAsync(Query(), default)).Items.Select(e => e.Id).ToList();

        Assert.Equal("e1", ids[0]);
        // Ties on time go by id descending as text: u9 sorts above u10.
        Assert.Equal(new[] { "u9", "u8", "u7", "u6", "u5", "u4", "u3", "u2", "u11", "u10", "u1" }, ids.Skip(1));
    }

    [ContractFact]
    public async Task Should_ExcludeUserEvents_When_NotIncluded()
    {
        var store = CreateStore();
        var seeded = await SeedEverythingAsync(store);
        var all = await MapperOutputAsync(store, seeded);

        var feed = await Feed(store).AuditEventsAsync(Query(includeUserEvents: false), default);

        Assert.DoesNotContain(feed.Items, e => e.Id.StartsWith('u'));
        Assert.Equal(all.Count - seeded.Users.Count, feed.Total);
        Assert.Equal(feed.Total, await Feed(store).AuditEventCountAsync(Query(includeUserEvents: false), default));
    }

    [ContractFact]
    public async Task Should_FilterByActorAndAction_When_Given()
    {
        var store = CreateStore();
        var seeded = await SeedEverythingAsync(store);
        var all = await MapperOutputAsync(store, seeded);
        string[] actions = [AuditActions.RowEdited, AuditActions.Validated, AuditActions.Received];

        var feed = await Feed(store).AuditEventsAsync(Query(actor: Admin, actions: actions), default);

        var expected = Expected(all.Where(e => e.ActorId == Admin && actions.Contains(e.Action))).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, feed.Items.Select(e => e.Id));
        Assert.Equal(expected.Count, feed.Total);
    }

    [ContractFact]
    public async Task Should_MatchQInOldNew_When_Searched()
    {
        var store = CreateStore();
        await SeedEverythingAsync(store);

        var feed = await Feed(store).AuditEventsAsync(Query(q: "ACME LTD"), default);

        var e = Assert.Single(feed.Items);
        Assert.Equal(AuditActions.RowEdited, e.Action);
        Assert.Equal("payee", e.Field);
        Assert.Equal("e2", e.RowExternalId);
    }

    [ContractFact]
    public async Task Should_MatchQInRequestReference_When_Searched()
    {
        var store = CreateStore();
        var seeded = await SeedEverythingAsync(store);
        var all = await MapperOutputAsync(store, seeded);

        var feed = await Feed(store).AuditEventsAsync(Query(q: "ref-b-7"), default);

        Assert.Equal(Expected(all.Where(e => e.RequestId == seeded.B)), feed.Items.Select(e => e.Id));
    }

    [ContractFact]
    public async Task Should_MatchQInPushIdHex_When_Searched()
    {
        var store = CreateStore();
        var seeded = await SeedEverythingAsync(store);

        var feed = await Feed(store).AuditEventsAsync(Query(q: "9B7D4C3E"), default);

        Assert.NotEmpty(feed.Items);
        Assert.All(feed.Items, e => Assert.Equal(seeded.B, e.RequestId));
    }

    [ContractFact]
    public async Task Should_KeepIdsStable_When_ReadTwice()
    {
        var store = CreateStore();
        await SeedEverythingAsync(store);

        var first = (await Feed(store).AuditEventsAsync(Query(), default)).Items.Select(e => e.Id).ToList();
        var second = (await Feed(store).AuditEventsAsync(Query(), default)).Items.Select(e => e.Id).ToList();

        Assert.Equal(first, second);
        Assert.Equal(first.Count, first.Distinct(StringComparer.Ordinal).Count());
    }

    [ContractFact]
    public async Task Should_FilterByTimeWindow_When_FromAndToGiven()
    {
        var store = CreateStore();
        await SeedEverythingAsync(store);

        var feed = await Feed(store).AuditEventsAsync(Query(from: T0.AddMinutes(3), to: T0.AddMinutes(5)), default);

        Assert.Equal(new[] { AuditActions.PostStarted, AuditActions.Validated }, feed.Items.Select(e => e.Action));
    }

    [ContractFact]
    public async Task Should_FilterByRequestReferenceAndPushId_When_Given()
    {
        var store = CreateStore();
        var seeded = await SeedEverythingAsync(store);
        var all = await MapperOutputAsync(store, seeded);
        var expected = Expected(all.Where(e => e.RequestId == seeded.B)).ToList();

        var byId = await Feed(store).AuditEventsAsync(Query(requestId: seeded.B), default);
        var byReference = await Feed(store).AuditEventsAsync(Query(reference: ReferenceB), default);
        var byPushId = await Feed(store).AuditEventsAsync(Query(pushId: PushB), default);

        Assert.Equal(expected, byId.Items.Select(e => e.Id));
        Assert.Equal(expected, byReference.Items.Select(e => e.Id));
        Assert.Equal(expected, byPushId.Items.Select(e => e.Id));
    }

    [ContractFact]
    public async Task Should_PageAndCount_When_PageSizeSmall()
    {
        var store = CreateStore();
        var seeded = await SeedEverythingAsync(store);
        var all = await MapperOutputAsync(store, seeded);

        var page2 = await Feed(store).AuditEventsAsync(Query(page: 2, pageSize: 5), default);

        Assert.Equal(all.Skip(5).Take(5).Select(e => e.Id), page2.Items.Select(e => e.Id));
        Assert.Equal(all.Count, page2.Total);
        Assert.Equal(all.Count, await Feed(store).AuditEventCountAsync(Query(page: 2, pageSize: 5), default));
    }
}
