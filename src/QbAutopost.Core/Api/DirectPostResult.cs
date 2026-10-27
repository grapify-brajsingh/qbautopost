using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Api;

/// <summary>A line QuickBooks accepted, with the identifiers an undo (FR-13) and an audit need.</summary>
public sealed record DirectPostedRow(
    int Index,
    string? ExternalId,
    string RequestId,
    string TxnId,
    string? EditSequence,
    TxnKind Kind,
    string Account,
    string? LineAccount,
    string? Payee,
    decimal Amount);

/// <summary>How the batch divided up. <c>Submitted</c> is what the caller sent, whatever became of it.</summary>
public sealed record DirectPostCounts(int Submitted, int Posted, int Held, int Skipped);

/// <summary>Money submitted and money that actually posted — never the same number by assumption.</summary>
public sealed record DirectPostTotals(decimal Submitted, decimal Posted);

/// <summary>
/// The result of one direct post (FR-A-8), and the body <c>GET /api/v1/batches/{id}</c> returns (FR-A-11). It is
/// persisted as <c>status.json</c> while the batch runs and as <c>result.json</c> when it ends, so a caller polling
/// after a restart reads the same shape the synchronous caller would have got.
/// </summary>
public sealed record DirectPostResult
{
    public required string BatchId { get; init; }

    public string? Reference { get; init; }

    /// <summary>The existing job states (spec §6): <c>ready</c> for a dry run, then <c>posting</c> → <c>posted</c>|<c>partial</c>|<c>failed</c>.</summary>
    public required JobStatus Status { get; init; }

    public bool DryRun { get; init; }

    public string? CompanyFile { get; init; }

    public DirectPostCounts Counts { get; init; } = new(0, 0, 0, 0);

    public DirectPostTotals Totals { get; init; } = new(0m, 0m);

    public IReadOnlyList<DirectPostedRow> Posted { get; init; } = [];

    /// <summary>Held rows, in the same shape the validate endpoint reports (FR-A-6), so one reader serves both.</summary>
    public IReadOnlyList<DirectPlanRow> Held { get; init; } = [];

    public IReadOnlyList<DirectPlanRow> Skipped { get; init; } = [];

    /// <summary>Why the batch did not finish cleanly; null when it did.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Nothing was sent because QuickBooks could not be reached or the app refused to send (the FR-11 backup guard).
    /// Carried explicitly rather than inferred from <see cref="Error"/>, so the endpoint answers §2's 503 without
    /// matching on a message — and so a caller can retry knowing the batch never reached QuickBooks.
    /// </summary>
    public bool Unavailable { get; init; }

    public DateTime StartedUtc { get; init; }

    public DateTime? FinishedUtc { get; init; }

    /// <summary>True while the worker still owns the batch, so a caller knows to keep polling.</summary>
    public bool IsRunning => JobStatusRules.IsActive(Status);
}
