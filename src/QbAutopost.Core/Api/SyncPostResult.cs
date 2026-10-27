using QbAutopost.Core.Jobs;

namespace QbAutopost.Core.Api;

/// <summary>
/// The 200 body of the synchronous post (validate-and-post §6.1): what landed in QuickBooks, with the
/// <c>TxnID</c>s, in the same response as the request that asked for it.
/// <para>
/// It is deliberately <i>not</i> <see cref="DirectPostResult"/>, though it is built from one. That record carries
/// <c>batchId</c>, <c>dryRun</c> and <c>isRunning</c> — a resource to poll and a mode to choose, which this route
/// does not have (§2.2, §2.3). Offering them here would invite a caller to poll a <c>GET</c> that will never accept
/// this id.
/// </para>
/// <para>
/// <see cref="PostId"/> <b>is</b> the batch id, suffix and all. §6.1's example drops the <c>#1</c>, but VP-8 requires
/// <c>POST /api/v1/batches/{postId}/undo</c> to reverse exactly this post, and undo keys off the full id; an id that
/// read prettily and undid nothing would be the worse of the two readings.
/// </para>
/// </summary>
public sealed record SyncPostResult
{
    /// <summary>Names the ledger row, for undo (FR-13). Not a polling resource: no <c>GET</c> accepts it (§6.1).</summary>
    public required string PostId { get; init; }

    public string? Reference { get; init; }

    /// <summary>
    /// <c>posted</c> when every row landed. §6.1 says it can be nothing else on a 200, and on the ordinary path it
    /// is not: §5.1 has already refused the request if any row would be held. It is carried honestly rather than
    /// hard-coded because the gates that run <i>after</i> the SDK is touched — G4's live half and G5 — can still
    /// refuse a row, and a caller whose money half-moved must be told, not reassured (SPEC-GAP T-923, Q-78).
    /// </summary>
    public required JobStatus Status { get; init; }

    public string? CompanyFile { get; init; }

    public DirectPostCounts Counts { get; init; } = new(0, 0, 0, 0);

    public DirectPostTotals Totals { get; init; } = new(0m, 0m);

    public IReadOnlyList<DirectPostedRow> Posted { get; init; } = [];

    /// <summary>
    /// Empty on the ordinary path — a held row is a 422 before anything is sent. It is here for the rows a
    /// post-SDK gate refused, which §6.1's shape has no other place for.
    /// </summary>
    public IReadOnlyList<DirectPlanRow> Held { get; init; } = [];

    /// <summary>
    /// Rows a skip rule (FR-6) or G4 excluded. A skip is not a hold: it is a standing instruction, or a transaction
    /// QuickBooks already has, so it does not refuse the batch — but it does explain why <c>counts.posted</c> is
    /// below <c>counts.submitted</c>, which is why it is reported rather than left as arithmetic a caller cannot do.
    /// </summary>
    public IReadOnlyList<DirectPlanRow> Skipped { get; init; } = [];

    public string? Error { get; init; }

    public DateTime StartedUtc { get; init; }

    public DateTime? FinishedUtc { get; init; }

    /// <summary>The batch as §6.1 presents it. Nothing is recomputed: every number comes from the post itself.</summary>
    public static SyncPostResult From(DirectPostResult batch) => new()
    {
        PostId = batch.BatchId,
        Reference = batch.Reference,
        Status = batch.Status,
        CompanyFile = batch.CompanyFile,
        Counts = batch.Counts,
        Totals = batch.Totals,
        Posted = batch.Posted,
        Held = batch.Held,
        Skipped = batch.Skipped,
        Error = batch.Error,
        StartedUtc = batch.StartedUtc,
        FinishedUtc = batch.FinishedUtc,
    };
}

/// <summary>
/// One reason a row would not post, in the caller's own terms (§6.2). <paramref name="Reason"/> is a
/// <c>HoldReasons</c> code; <paramref name="Detail"/> is the sentence beside it.
/// </summary>
public sealed record SyncPostFinding(
    int Index,
    string? ExternalId,
    string? Reason,
    string? Detail,
    IReadOnlyList<string>? Candidates,
    string? PostedIn)
{
    public static SyncPostFinding From(DirectPlanRow row) => new(
        row.Index,
        row.ExternalId,
        row.Reason,
        row.Note,
        row.Candidates.Count == 0 ? null : row.Candidates,
        row.BatchId);
}

/// <summary>
/// §6.2's arithmetic. <c>postable + held + skipped</c> is <c>submitted</c>, so a caller can check the answer as well
/// as read it. <c>skipped</c> is not in §6.2's example because its example has none; leaving it out where there are
/// some would make the numbers fail to add up, which is the one thing a count must never do.
/// </summary>
public sealed record SyncPostFindingCounts(int Submitted, int Postable, int Held, int Skipped);

/// <summary>
/// The 422 body of the synchronous post (§6.2): <b>every</b> reason the batch was refused, not the first.
/// <para>
/// All of them, because §5.1 refuses the whole batch on one held row: a caller who must fix everything before
/// re-sending would otherwise discover their problems one round trip at a time. It rides in the extensions of an
/// RFC 7807 problem, so the status and the detail still read correctly to a client that understands nothing else.
/// </para>
/// </summary>
public sealed record SyncPostFindings(
    SyncPostFindingCounts Counts,
    IReadOnlyList<SyncPostFinding> Validation,
    DirectUnknownNames UnknownNames)
{
    /// <summary>The held and duplicate rows of a plan. A skipped row is not a finding: nothing is wrong with it.</summary>
    public static SyncPostFindings From(DirectPlan plan)
    {
        var refused = plan.Rows
            .Where(r => r.Outcome is DirectOutcome.Held or DirectOutcome.Duplicate)
            .Select(SyncPostFinding.From)
            .ToList();

        return new SyncPostFindings(
            new SyncPostFindingCounts(plan.Submitted, plan.WouldPost, refused.Count, plan.Skipped - plan.Duplicates),
            refused,
            plan.UnknownNames);
    }

    /// <summary>The problem's <c>detail</c>: what was refused, and the assurance that matters most (§6.2).</summary>
    public string Detail =>
        $"{Counts.Held} of {Counts.Submitted} row(s) would be held; nothing was sent to QuickBooks.";
}
