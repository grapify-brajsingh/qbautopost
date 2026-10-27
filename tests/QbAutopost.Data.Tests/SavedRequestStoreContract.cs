using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>
/// What every <see cref="ISavedRequestStore"/> must do (spec §5.2, §9, §10). Store-agnostic: it reads only what the
/// interface returns, so the in-memory fake and the EF Core store run the same tests.
/// </summary>
public abstract class SavedRequestStoreContract
{
    protected static readonly DateTime T0 = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private const string RestartError = "the service restarted while this post was running";
    private const string Actor = "user:7";

    protected abstract ISavedRequestStore CreateStore();

    // ---- builders ------------------------------------------------------------------------------------------

    protected static string BodyOf(string reference = "R1", string payee = "Acme", string memo = "rent", decimal amount = 10m, string externalId = "e1") =>
        $$"""
        {"reference":"{{reference}}","controlTotal":{{amount:0.00}},"transactions":[{"externalId":"{{externalId}}","kind":"check","date":"2026-10-01","amount":{{amount:0.00}},"account":"Checking 1234","payee":"{{payee}}","memo":"{{memo}}"}]}
        """;

    private static ValidationSnapshot Snapshot(ValidationState state = ValidationState.Ok, int version = 1, DateTime? at = null) =>
        new(state, at ?? T0, version, null, [], [], null);

    private static IReadOnlySet<string> FingerprintsOf(PayloadRead read) =>
        read.Request is null
            ? new HashSet<string>()
            : DuplicateDetector.RowFingerprints(read.Request, new DirectLimits(), DateOnly.FromDateTime(T0));

    private static DuplicateMatch NoDuplicate => new(null, []);

    protected static NewSavedRequest NewRequest(
        string? body = null, string source = "api-client:n8n", string? key = null, DateTime? now = null, DuplicateMatch? duplicate = null)
    {
        body ??= BodyOf();
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var read = SavedPayloadReader.Read(payload);
        var state = read.Readable ? ValidationState.Ok : ValidationState.Unreadable;
        return new NewSavedRequest(
            source, source, body, payload, read, PayloadCanonicalizer.Sha256(payload), FingerprintsOf(read), key,
            Snapshot(state, 1, now ?? T0), duplicate ?? NoDuplicate, now ?? T0);
    }

    protected static NewVersion NewVersionOf(string body, string? note = null, DateTime? now = null, int version = 2)
    {
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var read = SavedPayloadReader.Read(payload);
        return new NewVersion(
            payload, read, PayloadCanonicalizer.Sha256(payload), FingerprintsOf(read), [], note,
            Snapshot(ValidationState.Ok, version, now ?? T0), NoDuplicate, Actor, now ?? T0);
    }

    private static async Task<SavedRequest> SaveAsync(ISavedRequestStore store, string? body = null, DateTime? now = null, string? key = null) =>
        (await store.CreateAsync(NewRequest(body, key: key, now: now), default)).Record;

    private static SavedRequestQuery Query(
        IReadOnlyList<SavedRequestStatus>? statuses = null, bool? needsAttention = null, bool? duplicate = null, string? q = null,
        int page = 1, int pageSize = 25, string sort = "-created") =>
        new(statuses ?? [], needsAttention, duplicate, q, null, null, null, sort, page, pageSize);

    private static AttemptCompletion Completion(
        AttemptOutcome outcome, SavedRequestStatus next, string? postId = null, string? error = null, DateTime? at = null) =>
        new(outcome, next, outcome == AttemptOutcome.Unknown ? null : 200, postId,
            JsonNode.Parse("""{"status":"x"}"""), JsonNode.Parse("""{"submitted":1,"posted":1,"held":0,"skipped":0}"""),
            error, at ?? T0.AddMinutes(1));

    private static async Task<(SavedRequest Claimed, long AttemptId)> ClaimAsync(ISavedRequestStore store, SavedRequest r)
    {
        var claim = await store.TryClaimAsync(r.Id, r.RowVersion, Actor, false, T0.AddMinutes(1), default);
        Assert.Equal(ClaimKind.Claimed, claim.Kind);
        return (claim.Record!, claim.AttemptId!.Value);
    }

    private static async Task<SavedRequest> DriveToAsync(
        ISavedRequestStore store, SavedRequestStatus status, AttemptOutcome outcome, string? postId)
    {
        var r = await SaveAsync(store);
        var (_, attemptId) = await ClaimAsync(store, r);
        await store.CompleteAttemptAsync(attemptId, Completion(outcome, status, postId), default);
        return (await store.GetAsync(r.Id, default))!;
    }

    private static async Task<IReadOnlyList<string>> KindsAsync(ISavedRequestStore store, long id) =>
        (await store.EventsAsync(id, default)).Select(e => e.Kind).ToList();

    // ---- create and idempotency ------------------------------------------------------------------------------

    [Fact]
    public async Task Should_StoreVersion1AndOriginalBody_When_Created()
    {
        var store = CreateStore();
        var body = BodyOf(reference: "INV-9", amount: 184.3m);

        var result = await store.CreateAsync(NewRequest(body, key: "k1"), default);

        Assert.Equal(CreateKind.Created, result.Kind);
        var r = (await store.GetAsync(result.Record.Id, default))!;
        Assert.Equal(SavedRequestStatus.Pending, r.Status);
        Assert.Equal(body, r.OriginalBody);
        Assert.Equal(1, r.CurrentVersion);
        Assert.Equal("INV-9", r.Reference);
        Assert.Equal(184.30m, r.ControlTotal);
        Assert.Equal(1, r.TransactionCount);
        Assert.Equal("k1", r.IdempotencyKey);
        Assert.False(r.NeedsAttention);
        Assert.Equal($"SR-{r.Id:000000}", r.PublicRef);
        Assert.Equal(T0, r.CreatedUtc);
        Assert.Null(r.LastAttempt);
        var versions = await store.VersionsAsync(r.Id, default);
        var v1 = Assert.Single(versions);
        Assert.Equal(1, v1.Number);
        Assert.True(v1.Readable);
        Assert.Empty(v1.Changes);
        Assert.Equal(PayloadCanonicalizer.Canonicalize(JsonDocument.Parse(body).RootElement), PayloadCanonicalizer.Canonicalize(v1.Payload));
    }

    [Fact]
    public async Task Should_ReplayExisting_When_SameSourceAndKeyAndBody()
    {
        var store = CreateStore();
        var first = await store.CreateAsync(NewRequest(key: "k1"), default);

        var second = await store.CreateAsync(NewRequest(key: "k1", now: T0.AddMinutes(5)), default);

        Assert.Equal(CreateKind.Replayed, second.Kind);
        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Equal(1, (await store.ListAsync(Query(), default)).Total);
        Assert.Single(await store.EventsAsync(first.Record.Id, default));
    }

    [Fact]
    public async Task Should_ReportKeyReused_When_SameKeyDifferentBody()
    {
        var store = CreateStore();
        await store.CreateAsync(NewRequest(key: "k1"), default);

        var second = await store.CreateAsync(NewRequest(BodyOf(reference: "OTHER"), key: "k1"), default);

        Assert.Equal(CreateKind.KeyReused, second.Kind);
        Assert.Equal(1, (await store.ListAsync(Query(), default)).Total);
    }

    [Fact]
    public async Task Should_CreateSeparately_When_SameKeyDifferentSource()
    {
        var store = CreateStore();
        var a = await store.CreateAsync(NewRequest(key: "k1", source: "api-client:a"), default);

        var b = await store.CreateAsync(NewRequest(key: "k1", source: "api-client:b"), default);

        Assert.Equal(CreateKind.Created, b.Kind);
        Assert.NotEqual(a.Record.Id, b.Record.Id);
    }

    // ---- editing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_AppendVersionAndBumpRowVersion_When_ExpectedRowVersionMatches()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);

        var result = await store.AppendVersionAsync(r.Id, r.RowVersion, NewVersionOf(BodyOf(reference: "R2", amount: 25m), "fixed amount", T0.AddHours(1)), default);

        Assert.Equal(MutationKind.Ok, result.Kind);
        var after = result.Record!;
        Assert.Equal(2, after.CurrentVersion);
        Assert.Equal(r.RowVersion + 1, after.RowVersion);
        Assert.Equal("R2", after.Reference);
        Assert.Equal(25.00m, after.ControlTotal);
        Assert.Equal(Actor, after.UpdatedBy);
        Assert.Equal(T0.AddHours(1), after.UpdatedUtc);
        var v2 = (await store.VersionAsync(r.Id, 2, default))!;
        Assert.Equal("fixed amount", v2.Note);
        Assert.Equal(Actor, v2.CreatedBy);
        Assert.Equal(2, (await store.VersionsAsync(r.Id, default)).Count);
    }

    [Fact]
    public async Task Should_ReturnStale_When_ExpectedRowVersionIsOld()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);

        var result = await store.AppendVersionAsync(r.Id, r.RowVersion + 5, NewVersionOf(BodyOf(reference: "R2")), default);

        Assert.Equal(MutationKind.Stale, result.Kind);
        Assert.Single(await store.VersionsAsync(r.Id, default));
    }

    [Fact]
    public async Task Should_ReturnWrongStatus_When_AppendingToNonPending()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        var (claimed, _) = await ClaimAsync(store, r);

        var result = await store.AppendVersionAsync(r.Id, claimed.RowVersion, NewVersionOf(BodyOf(reference: "R2")), default);

        Assert.Equal(MutationKind.WrongStatus, result.Kind);
        Assert.Single(await store.VersionsAsync(r.Id, default));
    }

    [Fact]
    public async Task Should_ReturnUnchanged_When_CanonicalHashEqualsCurrent()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        // Same content as version 1, different key order and spacing: same canonical hash.
        var reordered = """
            { "transactions": [ {"memo":"rent","payee":"Acme","account":"Checking 1234","amount":10,"date":"2026-10-01","kind":"check","externalId":"e1"} ],
              "controlTotal": 10.0, "reference": "R1" }
            """;

        var result = await store.AppendVersionAsync(r.Id, r.RowVersion, NewVersionOf(reordered), default);

        Assert.Equal(MutationKind.Unchanged, result.Kind);
        Assert.Single(await store.VersionsAsync(r.Id, default));
        Assert.Equal(r.RowVersion, (await store.GetAsync(r.Id, default))!.RowVersion);
        Assert.Single(await store.EventsAsync(r.Id, default));
    }

    [Fact]
    public async Task Should_KeepVersion1Unchanged_When_EditedThreeTimes()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        var v1Before = (await store.VersionAsync(r.Id, 1, default))!;
        var rowVersion = r.RowVersion;

        for (var i = 1; i <= 3; i++)
        {
            var result = await store.AppendVersionAsync(r.Id, rowVersion, NewVersionOf(BodyOf(reference: $"E{i}"), version: i + 1), default);
            Assert.Equal(MutationKind.Ok, result.Kind);
            rowVersion = result.Record!.RowVersion;
        }

        var after = (await store.GetAsync(r.Id, default))!;
        Assert.Equal(4, after.CurrentVersion);
        Assert.Equal(r.OriginalBody, after.OriginalBody);
        Assert.Equal(r.OriginalSha256, after.OriginalSha256);
        var v1After = (await store.VersionAsync(r.Id, 1, default))!;
        Assert.Equal(v1Before.Payload.GetRawText(), v1After.Payload.GetRawText());
        Assert.Equal(new[] { 1, 2, 3, 4 }, (await store.VersionsAsync(r.Id, default)).Select(v => v.Number));
    }

    // ---- claiming and completing -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_ClaimOnce_When_TwoClaimsRace()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);

        var results = await Task.WhenAll(
            Task.Run(() => store.TryClaimAsync(r.Id, null, "user:1", false, T0.AddMinutes(1), default)),
            Task.Run(() => store.TryClaimAsync(r.Id, null, "user:2", false, T0.AddMinutes(1), default)));

        var winner = Assert.Single(results, c => c.Kind == ClaimKind.Claimed);
        var loser = Assert.Single(results, c => c.Kind == ClaimKind.AlreadyPosting);
        Assert.Equal(winner.AttemptId, loser.AttemptId);
        Assert.Single(await store.AttemptsAsync(r.Id, default));
        var after = (await store.GetAsync(r.Id, default))!;
        Assert.Equal(SavedRequestStatus.Posting, after.Status);
        Assert.Equal(r.RowVersion + 1, after.RowVersion);
    }

    [Fact]
    public async Task Should_StartInProgressAttempt_When_Claimed()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        await store.AppendVersionAsync(r.Id, r.RowVersion, NewVersionOf(BodyOf(reference: "R2")), default);
        var current = (await store.GetAsync(r.Id, default))!;

        var claim = await store.TryClaimAsync(r.Id, current.RowVersion, Actor, false, T0.AddMinutes(1), default);

        Assert.Equal(ClaimKind.Claimed, claim.Kind);
        Assert.Equal(2, claim.VersionNumber);
        var attempt = (await store.AttemptAsync(r.Id, claim.AttemptId!.Value, default))!;
        Assert.Equal(AttemptOutcome.InProgress, attempt.Outcome);
        Assert.Equal(2, attempt.VersionNumber);
        Assert.Equal(Actor, attempt.StartedBy);
        Assert.Equal(T0.AddMinutes(1), attempt.StartedUtc);
        Assert.Null(attempt.FinishedUtc);
        Assert.Equal(attempt.Id, claim.Record!.LastAttempt!.Id);
        Assert.Equal(SavedRequestStatus.Posting, claim.Record.Status);
    }

    [Fact]
    public async Task Should_RecordDuplicateAcknowledgement_When_ClaimAcknowledges()
    {
        var store = CreateStore();
        var plain = await SaveAsync(store);
        var acknowledged = await SaveAsync(store, BodyOf(reference: "R2"));
        var claimTime = T0.AddMinutes(3);

        await store.TryClaimAsync(plain.Id, null, Actor, false, claimTime, default);
        var claim = await store.TryClaimAsync(acknowledged.Id, null, "user:9", true, claimTime, default);

        Assert.Equal(ClaimKind.Claimed, claim.Kind);
        var after = (await store.GetAsync(acknowledged.Id, default))!;
        Assert.Equal("user:9", after.DuplicateAcknowledgedBy);
        Assert.Equal(claimTime, after.DuplicateAcknowledgedUtc);
        var untouched = (await store.GetAsync(plain.Id, default))!;
        Assert.Null(untouched.DuplicateAcknowledgedBy);
        Assert.Null(untouched.DuplicateAcknowledgedUtc);
    }

    [Fact]
    public async Task Should_RefuseClaim_When_Posted()
    {
        var store = CreateStore();
        var posted = await DriveToAsync(store, SavedRequestStatus.Posted, AttemptOutcome.Posted, "P1");

        var claim = await store.TryClaimAsync(posted.Id, null, Actor, false, T0.AddHours(1), default);

        Assert.Equal(ClaimKind.AlreadyPosted, claim.Kind);
        Assert.Single(await store.AttemptsAsync(posted.Id, default));
    }

    [Fact]
    public async Task Should_RefuseClaim_When_Discarded()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        await store.ChangeStatusAsync(r.Id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Actor, T0, SavedRequestEvents.Discarded, null, default);

        var claim = await store.TryClaimAsync(r.Id, null, Actor, false, T0.AddHours(1), default);

        Assert.Equal(ClaimKind.NotPostable, claim.Kind);
        Assert.Empty(await store.AttemptsAsync(r.Id, default));
    }

    [Theory]
    [InlineData(SavedRequestStatus.Unknown, AttemptOutcome.Unknown)]
    [InlineData(SavedRequestStatus.Partial, AttemptOutcome.Partial)]
    public async Task Should_RefuseClaim_When_Unknown(SavedRequestStatus status, AttemptOutcome outcome)
    {
        var store = CreateStore();
        var r = await DriveToAsync(store, status, outcome, null);

        var claim = await store.TryClaimAsync(r.Id, null, Actor, false, T0.AddHours(1), default);

        Assert.Equal(ClaimKind.NotPostable, claim.Kind);
        Assert.Single(await store.AttemptsAsync(r.Id, default));
    }

    [Fact]
    public async Task Should_ReturnNotFound_When_ClaimingMissingRequest()
    {
        var claim = await CreateStore().TryClaimAsync(999, null, Actor, false, T0, default);

        Assert.Equal(ClaimKind.NotFound, claim.Kind);
        Assert.Null(claim.AttemptId);
    }

    [Fact]
    public async Task Should_ReturnStale_When_ClaimExpectedRowVersionIsOld()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);

        var claim = await store.TryClaimAsync(r.Id, r.RowVersion + 1, Actor, false, T0.AddMinutes(1), default);

        Assert.Equal(ClaimKind.Stale, claim.Kind);
        Assert.Equal(SavedRequestStatus.Pending, (await store.GetAsync(r.Id, default))!.Status);
        Assert.Empty(await store.AttemptsAsync(r.Id, default));
    }

    [Fact]
    public async Task Should_RecordAttemptAndMoveStatus_When_Completed()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        var (claimed, attemptId) = await ClaimAsync(store, r);
        var finished = T0.AddMinutes(2);

        await store.CompleteAttemptAsync(attemptId, Completion(AttemptOutcome.Posted, SavedRequestStatus.Posted, "P-77", at: finished), default);

        var after = (await store.GetAsync(r.Id, default))!;
        Assert.Equal(SavedRequestStatus.Posted, after.Status);
        Assert.Equal("P-77", after.PostId);
        Assert.Equal(claimed.RowVersion + 1, after.RowVersion);
        var attempt = (await store.AttemptAsync(r.Id, attemptId, default))!;
        Assert.Equal(AttemptOutcome.Posted, attempt.Outcome);
        Assert.Equal(200, attempt.HttpStatus);
        Assert.Equal("P-77", attempt.PostId);
        Assert.Equal(finished, attempt.FinishedUtc);
        Assert.Equal("x", attempt.Result!["status"]!.GetValue<string>());
        Assert.Equal(1, attempt.Counts!["posted"]!.GetValue<int>());
        Assert.Equal(attemptId, after.LastAttempt!.Id);
    }

    [Fact]
    public async Task Should_ReturnToPendingWithoutPostId_When_CompletedAsHeld()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        var (_, attemptId) = await ClaimAsync(store, r);

        await store.CompleteAttemptAsync(attemptId, Completion(AttemptOutcome.Held, SavedRequestStatus.Pending), default);

        var after = (await store.GetAsync(r.Id, default))!;
        Assert.Equal(SavedRequestStatus.Pending, after.Status);
        Assert.Null(after.PostId);
        Assert.Equal(AttemptOutcome.Held, (await store.AttemptAsync(r.Id, attemptId, default))!.Outcome);
    }

    [Fact]
    public async Task Should_Throw_When_CompletingAnAttemptTwice()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        var (_, attemptId) = await ClaimAsync(store, r);
        await store.CompleteAttemptAsync(attemptId, Completion(AttemptOutcome.Posted, SavedRequestStatus.Posted, "P1"), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CompleteAttemptAsync(attemptId, Completion(AttemptOutcome.Failed, SavedRequestStatus.Pending), default));

        Assert.Equal(SavedRequestStatus.Posted, (await store.GetAsync(r.Id, default))!.Status);
    }

    [Fact]
    public async Task Should_MarkUnknown_When_RecoveringInFlight()
    {
        var store = CreateStore();
        var inFlight = await SaveAsync(store);
        var idle = await SaveAsync(store, BodyOf(reference: "R2"));
        var (_, attemptId) = await ClaimAsync(store, inFlight);
        var restart = T0.AddHours(2);

        var moved = await store.RecoverInFlightAsync(restart, default);

        Assert.Equal(1, moved);
        var after = (await store.GetAsync(inFlight.Id, default))!;
        Assert.Equal(SavedRequestStatus.Unknown, after.Status);
        var attempt = (await store.AttemptAsync(inFlight.Id, attemptId, default))!;
        Assert.Equal(AttemptOutcome.Unknown, attempt.Outcome);
        Assert.Equal(RestartError, attempt.Error);
        Assert.Equal(restart, attempt.FinishedUtc);
        Assert.Equal(SavedRequestStatus.Pending, (await store.GetAsync(idle.Id, default))!.Status);
        Assert.Equal(0, await store.RecoverInFlightAsync(restart, default));
    }

    // ---- resolving ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ResolvePartialOnlyToPosted()
    {
        var store = CreateStore();
        var partial = await DriveToAsync(store, SavedRequestStatus.Partial, AttemptOutcome.Partial, "P-5");

        var refused = await store.ResolveAsync(partial.Id, ResolveOutcome.NotPosted, "retry", "user:admin", T0.AddHours(1), default);
        var accepted = await store.ResolveAsync(partial.Id, ResolveOutcome.Posted, "accepted as is", "user:admin", T0.AddHours(2), default);

        Assert.Equal(MutationKind.WrongStatus, refused.Kind);
        Assert.Equal(MutationKind.Ok, accepted.Kind);
        Assert.Equal(SavedRequestStatus.Posted, accepted.Record!.Status);
        Assert.Equal("P-5", accepted.Record.PostId);
        var attempt = (await store.AttemptsAsync(partial.Id, default)).Single();
        Assert.Equal("user:admin", attempt.ResolvedBy);
        Assert.Equal(T0.AddHours(2), attempt.ResolvedUtc);
        Assert.Equal("accepted as is", attempt.ResolutionNote);
    }

    [Fact]
    public async Task Should_ReturnToPending_When_UnknownResolvedNotPosted()
    {
        var store = CreateStore();
        var unknown = await DriveToAsync(store, SavedRequestStatus.Unknown, AttemptOutcome.Unknown, null);

        var result = await store.ResolveAsync(unknown.Id, ResolveOutcome.NotPosted, "checked QuickBooks", "user:admin", T0.AddHours(1), default);

        Assert.Equal(MutationKind.Ok, result.Kind);
        Assert.Equal(SavedRequestStatus.Pending, result.Record!.Status);
        Assert.Null(result.Record.PostId);
        Assert.Equal("checked QuickBooks", (await store.AttemptsAsync(unknown.Id, default)).Single().ResolutionNote);
    }

    [Fact]
    public async Task Should_ReturnWrongStatus_When_ResolvingPendingRequest()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);

        var result = await store.ResolveAsync(r.Id, ResolveOutcome.Posted, "n", "user:admin", T0, default);

        Assert.Equal(MutationKind.WrongStatus, result.Kind);
    }

    // ---- status changes and validation ------------------------------------------------------------------------

    [Fact]
    public async Task Should_StoreDiscardReasonAndClearItOnRestore_When_StatusChanges()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);

        var discarded = await store.ChangeStatusAsync(
            r.Id, r.RowVersion, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Actor, T0.AddMinutes(1),
            SavedRequestEvents.Discarded, JsonNode.Parse("""{"reason":"duplicate of SR-000009"}"""), default);
        var wrong = await store.ChangeStatusAsync(
            r.Id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Actor, T0, SavedRequestEvents.Discarded, null, default);
        var stale = await store.ChangeStatusAsync(
            r.Id, r.RowVersion, SavedRequestStatus.Discarded, SavedRequestStatus.Pending, Actor, T0, SavedRequestEvents.Restored, null, default);
        var restored = await store.ChangeStatusAsync(
            r.Id, discarded.Record!.RowVersion, SavedRequestStatus.Discarded, SavedRequestStatus.Pending, "user:admin", T0.AddMinutes(2),
            SavedRequestEvents.Restored, null, default);

        Assert.Equal("duplicate of SR-000009", discarded.Record.DiscardReason);
        Assert.Equal(r.RowVersion + 1, discarded.Record.RowVersion);
        Assert.Equal(MutationKind.WrongStatus, wrong.Kind);
        Assert.Equal(MutationKind.Stale, stale.Kind);
        Assert.Equal(SavedRequestStatus.Pending, restored.Record!.Status);
        Assert.Null(restored.Record.DiscardReason);
    }

    [Fact]
    public async Task Should_StoreSnapshotAndFlag_When_SettingValidation()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        var snapshot = Snapshot(ValidationState.Held, 1, T0.AddMinutes(4));

        var result = await store.SetValidationAsync(r.Id, snapshot, new DuplicateMatch(42, [DuplicateReasons.SamePayload]), Actor, T0.AddMinutes(4), default);

        Assert.Equal(MutationKind.Ok, result.Kind);
        var after = result.Record!;
        Assert.True(after.NeedsAttention);
        Assert.Equal(ValidationState.Held, after.Validation!.State);
        Assert.Equal(T0.AddMinutes(4), after.ValidatedUtc);
        Assert.Equal(42, after.DuplicateOfId);
        Assert.Equal(new[] { DuplicateReasons.SamePayload }, after.DuplicateReasons);
        Assert.Equal(r.RowVersion + 1, after.RowVersion);

        var stale = await store.SetValidationAsync(r.Id, Snapshot(ValidationState.Ok, 9), NoDuplicate, Actor, T0, default);
        Assert.Equal(MutationKind.Stale, stale.Kind);
    }

    [Fact]
    public async Task Should_FlagNeedsAttention_When_PayloadIsUnreadable()
    {
        var store = CreateStore();

        var r = await SaveAsync(store, """{"transactions":5}""");

        Assert.True(r.NeedsAttention);
        var v1 = (await store.VersionAsync(r.Id, 1, default))!;
        Assert.False(v1.Readable);
        Assert.NotEmpty(v1.ReadErrors);
        Assert.Equal(0, r.TransactionCount);
    }

    // ---- listing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_FilterAndPage_When_Listing()
    {
        var store = CreateStore();
        var a = await SaveAsync(store, BodyOf(reference: "ALPHA-1", payee: "Pepco", memo: "electric"), T0.AddMinutes(1));
        var b = await SaveAsync(store, BodyOf(reference: "BRAVO-2", payee: "Sysco", memo: "produce"), T0.AddMinutes(2));
        var c = await SaveAsync(store, BodyOf(reference: "CHARLIE-3", payee: "Giant", memo: "Pepco refund"), T0.AddMinutes(3));
        var d = await SaveAsync(store, """{"transactions":5}""", T0.AddMinutes(4));
        await store.ChangeStatusAsync(b.Id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Actor, T0.AddMinutes(5), SavedRequestEvents.Discarded, null, default);

        var all = await store.ListAsync(Query(), default);
        var discarded = await store.ListAsync(Query([SavedRequestStatus.Discarded]), default);
        var either = await store.ListAsync(Query([SavedRequestStatus.Discarded, SavedRequestStatus.Posted]), default);
        var attention = await store.ListAsync(Query(needsAttention: true), default);
        var byReference = await store.ListAsync(Query(q: "bravo"), default);
        var byPayeeAndMemo = await store.ListAsync(Query(q: "pepco"), default);
        var byPublicRef = await store.ListAsync(Query(q: a.PublicRef), default);
        var firstPage = await store.ListAsync(Query(page: 1, pageSize: 3, sort: "created"), default);
        var lastPage = await store.ListAsync(Query(page: 2, pageSize: 3, sort: "created"), default);

        Assert.Equal(4, all.Total);
        Assert.Equal(new[] { d.Id, c.Id, b.Id, a.Id }, all.Items.Select(i => i.Id));
        Assert.Equal(new[] { b.Id }, discarded.Items.Select(i => i.Id));
        Assert.Equal(1, either.Total);
        Assert.Equal(new[] { d.Id }, attention.Items.Select(i => i.Id));
        Assert.Equal(new[] { b.Id }, byReference.Items.Select(i => i.Id));
        Assert.Equal(new[] { c.Id, a.Id }, byPayeeAndMemo.Items.Select(i => i.Id));
        Assert.Equal(new[] { a.Id }, byPublicRef.Items.Select(i => i.Id));
        Assert.Equal(4, firstPage.Total);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, firstPage.Items.Select(i => i.Id));
        Assert.Equal(new[] { d.Id }, lastPage.Items.Select(i => i.Id));
        Assert.Equal(2, lastPage.Page);
        Assert.Equal(3, lastPage.PageSize);
    }

    [Fact]
    public async Task Should_FilterByDuplicateFlag_When_Listing()
    {
        var store = CreateStore();
        var original = await SaveAsync(store);
        var copy = (await store.CreateAsync(NewRequest(duplicate: new DuplicateMatch(original.Id, [DuplicateReasons.SamePayload])), default)).Record;

        var flagged = await store.ListAsync(Query(duplicate: true), default);
        var unflagged = await store.ListAsync(Query(duplicate: false), default);

        Assert.Equal(new[] { copy.Id }, flagged.Items.Select(i => i.Id));
        Assert.Equal(new[] { original.Id }, unflagged.Items.Select(i => i.Id));
        Assert.Equal(original.Id, flagged.Items.Single().DuplicateOfId);
    }

    [Fact]
    public async Task Should_CountByStatus_When_Stats()
    {
        var store = CreateStore();
        await SaveAsync(store);
        await SaveAsync(store, """{"transactions":5}""");
        await DriveToAsync(store, SavedRequestStatus.Posted, AttemptOutcome.Posted, "P1");

        var stats = await store.StatsAsync(default);

        Assert.Equal(3, stats.Total);
        Assert.Equal(2, stats.ByStatus.GetValueOrDefault(SavedRequestStatus.Pending));
        Assert.Equal(1, stats.ByStatus.GetValueOrDefault(SavedRequestStatus.Posted));
        Assert.Equal(1, stats.NeedsAttention);
    }

    [Fact]
    public async Task Should_ExcludeDiscardedAndOld_When_ListingDuplicateCandidates()
    {
        var store = CreateStore();
        var old = await SaveAsync(store, BodyOf(reference: "OLD"), T0.AddDays(-10));
        var recent = await SaveAsync(store, BodyOf(reference: "RECENT"), T0);
        var discarded = await SaveAsync(store, BodyOf(reference: "GONE"), T0);
        await store.ChangeStatusAsync(discarded.Id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Actor, T0, SavedRequestEvents.Discarded, null, default);

        var candidates = await store.DuplicateCandidatesAsync(T0.AddDays(-1), null, default);
        var excluding = await store.DuplicateCandidatesAsync(T0.AddDays(-1), recent.Id, default);

        var digest = Assert.Single(candidates);
        Assert.Equal(recent.Id, digest.Id);
        Assert.Equal("RECENT", digest.Reference);
        Assert.Equal(recent.CanonicalSha256, digest.CanonicalSha256);
        Assert.Equal(T0, digest.CreatedUtc);
        Assert.NotEmpty(digest.RowFingerprints);
        Assert.Empty(excluding);
        Assert.DoesNotContain(candidates, c => c.Id == old.Id || c.Id == discarded.Id);
    }

    [Fact]
    public async Task Should_ReturnLatestVersionFingerprints_When_ListingCandidatesAfterAppend()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        var v1 = (await store.DuplicateCandidatesAsync(T0.AddDays(-1), null, default)).Single().RowFingerprints.ToHashSet();
        await store.AppendVersionAsync(r.Id, r.RowVersion, NewVersionOf(BodyOf(reference: "R2", amount: 25m, externalId: "e2")), default);

        var v2 = (await store.DuplicateCandidatesAsync(T0.AddDays(-1), null, default)).Single().RowFingerprints.ToHashSet();

        Assert.NotEmpty(v1);
        Assert.NotEmpty(v2);
        Assert.False(v1.Overlaps(v2));
    }

    // ---- events ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_WriteOneEventPerMutation()
    {
        var store = CreateStore();
        var r = await SaveAsync(store);
        Assert.Equal(new[] { SavedRequestEvents.Created }, await KindsAsync(store, r.Id));

        var edited = await store.AppendVersionAsync(r.Id, r.RowVersion, NewVersionOf(BodyOf(reference: "R2")), default);
        var validated = await store.SetValidationAsync(r.Id, Snapshot(ValidationState.Ok, 2), NoDuplicate, Actor, T0, default);
        var discarded = await store.ChangeStatusAsync(r.Id, null, SavedRequestStatus.Pending, SavedRequestStatus.Discarded, Actor, T0, SavedRequestEvents.Discarded, null, default);
        await store.ChangeStatusAsync(r.Id, null, SavedRequestStatus.Discarded, SavedRequestStatus.Pending, Actor, T0, SavedRequestEvents.Restored, null, default);
        var current = (await store.GetAsync(r.Id, default))!;
        var (_, attemptId) = await ClaimAsync(store, current);
        await store.CompleteAttemptAsync(attemptId, Completion(AttemptOutcome.Unknown, SavedRequestStatus.Unknown), default);
        await store.ResolveAsync(r.Id, ResolveOutcome.NotPosted, "checked", "user:admin", T0, default);

        Assert.Equal(MutationKind.Ok, edited.Kind);
        Assert.Equal(MutationKind.Ok, validated.Kind);
        Assert.Equal(MutationKind.Ok, discarded.Kind);
        Assert.Equal(
            new[]
            {
                SavedRequestEvents.Created, SavedRequestEvents.Edited, SavedRequestEvents.Validated, SavedRequestEvents.Discarded,
                SavedRequestEvents.Restored, SavedRequestEvents.PostStarted, SavedRequestEvents.PostFinished, SavedRequestEvents.Resolved,
            },
            await KindsAsync(store, r.Id));
    }

    [Fact]
    public async Task Should_WriteFlaggedDuplicateEvent_When_CreatedAsDuplicate()
    {
        var store = CreateStore();
        var original = await SaveAsync(store);

        var copy = (await store.CreateAsync(NewRequest(duplicate: new DuplicateMatch(original.Id, [DuplicateReasons.SamePayload])), default)).Record;

        Assert.Equal(new[] { SavedRequestEvents.Created, SavedRequestEvents.FlaggedDuplicate }, await KindsAsync(store, copy.Id));
        Assert.Equal(original.Id, copy.DuplicateOfId);
        Assert.Equal(new[] { DuplicateReasons.SamePayload }, copy.DuplicateReasons);
    }
}
