using QbAutopost.Core.Api;

namespace QbAutopost.Api.Jobs;

/// <summary>Which of validate-and-post §5's five endings a request reached.</summary>
public enum SyncPostOutcome
{
    /// <summary>The request could not be read at all (§6.3). 400, nothing planned, nothing posted.</summary>
    Refused,

    /// <summary>At least one row would be held, so the whole request is refused (§5.1). 422, nothing sent.</summary>
    Held,

    /// <summary>The QuickBooks session is in use (§6.4). 409, nothing enqueued, nothing sent.</summary>
    Busy,

    /// <summary>The post ran. <see cref="SyncPost.Result"/> says what QuickBooks did with it.</summary>
    Posted,
}

/// <summary>
/// One ending, with only the part that belongs to it filled in — so a caller of this runner cannot answer 200 with
/// findings, or 422 with transaction ids.
/// </summary>
public sealed record SyncPost(
    SyncPostOutcome Outcome,
    IReadOnlyList<string> Errors,
    SyncPostFindings? Findings,
    SyncPostResult? Result,
    bool Unavailable);

/// <summary>
/// The synchronous validate-and-post (ADR-0008). It is almost entirely other people's code: <see cref="DirectPlanner"/>
/// decides every row, and <see cref="DirectPostRunner"/> performs the post — the backup guard, G4's live half, the
/// qbXML, the SDK call, G5 and the ledger row — exactly as it does for FR-A-8. Nothing about posting is reimplemented
/// here, so the two routes cannot drift into disagreeing about what a row means.
/// <para>
/// What <i>is</i> new is the gate between them. FR-A-8 posts the rows that passed and reports the rest; this route
/// refuses the batch if <b>any</b> row would be held, and never opens a session (§5.1). The whole feature is that
/// early return, and the test that proves the gateway received nothing.
/// </para>
/// <para>
/// The second new thing is that the worker lock is <i>attempted</i>, not waited for (§2.1): busy means 409 now,
/// rather than a wait in a queue the caller cannot see.
/// </para>
/// </summary>
public sealed class SyncPostRunner(JobQueue queue, DirectPostRunner runner, ILogger<SyncPostRunner> log)
{
    /// <param name="onSending">
    /// T-1014 (ruling R3): called as the first step of the work on the worker, before the batch is begun. A caller that
    /// sees an exception can then tell "nothing was started" (never called) from "QuickBooks may have it" (called).
    /// A busy ending never calls it.
    /// </param>
    public async Task<SyncPost> RunAsync(SyncPostRequest request, CancellationToken ct, Action? onSending = null)
    {
        if (UnsupportedFields(request) is { Count: > 0 } unsupported)
        {
            log.LogWarning(
                "Sync post refused for reference {Reference}: {Errors}", request.Reference, string.Join("; ", unsupported));
            return Ending(SyncPostOutcome.Refused, unsupported);
        }

        var direct = request.ToDirectRequest();
        var plan = runner.Plan(direct);
        if (plan.Errors.Count > 0)
        {
            // Refused before it has an identity: no post id, no evidence folder, nothing to undo later.
            log.LogWarning(
                "Sync post refused {Rows} row(s) for reference {Reference}: {Errors}",
                plan.Submitted, request.Reference, string.Join("; ", plan.Errors));
            return Ending(SyncPostOutcome.Refused, plan.Errors);
        }

        if (!plan.Ok)
        {
            // §5.1, and the point of the route. Every finding at once, and not one byte to QuickBooks.
            var findings = SyncPostFindings.From(plan);
            log.LogInformation(
                "Sync post held {Held} of {Submitted} row(s) for reference {Reference}; nothing was sent",
                findings.Counts.Held, plan.Submitted, request.Reference);
            return new SyncPost(SyncPostOutcome.Held, [], findings, null, false);
        }

        var label = runner.JobIdOf(direct);
        // §7: `ct` (the caller's connection) bounds only the *wait*. The work runs on the worker under the worker's
        // own token, so a client that disconnects mid-post does not stop it — abandoning a half-sent qbXML would
        // leave QuickBooks in a state nobody recorded. The ledger row is written either way, and §8 is how the
        // caller finds out what happened.
        var (ran, result) = await queue.TryRunExclusiveAsync(
            label,
            workerCt =>
            {
                onSending?.Invoke();
                return PostAsync(direct, plan, workerCt);
            },
            ct);

        if (!ran)
        {
            log.LogInformation(
                "Sync post for reference {Reference} refused: the QuickBooks session is in use; nothing was sent",
                request.Reference);
            return Ending(SyncPostOutcome.Busy, []);
        }

        log.LogInformation(
            "Sync post {PostId}: {Posted} posted, {Held} held, {Skipped} skipped",
            result!.BatchId, result.Counts.Posted, result.Counts.Held, result.Counts.Skipped);
        return new SyncPost(SyncPostOutcome.Posted, [], null, SyncPostResult.From(result), result.Unavailable);
    }

    /// <summary>
    /// The post itself, on the worker. <see cref="DirectPostRunner.Begin"/> puts the request and the opening state
    /// on disk before <see cref="DirectPostRunner.RunAsync"/> sends anything (CLAUDE.md rule 7), and the post id is
    /// taken here rather than earlier so a refused or busy request leaves no evidence folder behind.
    /// <para>
    /// The plan was made before the lock was claimed, so the ledger it read is a moment old. That is not a hole: a
    /// row another batch posted in the meantime is caught by G4's <i>live</i> half inside the post, which asks
    /// QuickBooks rather than the ledger, and is skipped there.
    /// </para>
    /// </summary>
    private async Task<DirectPostResult> PostAsync(DirectRequest request, DirectPlan plan, CancellationToken ct)
    {
        var postId = runner.NextBatchId(request);
        runner.Begin(postId, request, plan, dryRun: false);
        log.LogInformation("Sync post {PostId}: posting {Rows} row(s)", postId, plan.WouldPost);
        return await runner.RunAsync(postId, request, plan, ct);
    }

    /// <summary>
    /// The two fields this route does not have (§2.3, §4). They are refused rather than ignored: a caller who sends
    /// <c>dryRun</c> believes nothing will be posted, and answering them "posted" would be the most expensive kind
    /// of silence. This follows T-915, which refused <c>allowModelAccounts</c> for the same reason.
    /// T-1012: public so a saved request's validation snapshot records the same two refusals in the same words.
    /// </summary>
    public static IReadOnlyList<string> UnsupportedFields(SyncPostRequest request)
    {
        var errors = new List<string>();
        if (request.DryRun is not null)
        {
            errors.Add(
                "dry-run-not-supported: this route always posts, so it has no dryRun field and will not pretend to "
                + "honour one. Use POST /api/v1/quickbooks/transactions/validate to check a batch without sending it, "
                + "or POST /api/v1/quickbooks/transactions for the dry-run and batch behaviour of FR-A-8");
        }

        if (request.AllowModelAccounts is not null)
        {
            errors.Add(
                "allowModelAccounts is not implemented on this path either: tiers 3-4 never run here, so the flag "
                + "would silently do nothing. Omit it and give each row a lineAccount, or add a rule for its last four "
                + "(Q-51 records the decision the owner still has to make)");
        }

        return errors;
    }

    private static SyncPost Ending(SyncPostOutcome outcome, IReadOnlyList<string> errors) =>
        new(outcome, errors, null, null, false);
}
