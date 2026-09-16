using QbAutopost.Core.Gates;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Output;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Endpoints;

/// <summary><c>GET /jobs/{id}</c> (spec §6 JobView): the job record plus the job's latest <c>result.json</c>.</summary>
public sealed record JobView
{
    public required string JobId { get; init; }
    public required JobStatus Status { get; init; }
    public bool DryRun { get; init; }
    public string? Company { get; init; }
    public required string Folder { get; init; }
    public IReadOnlyList<StatementView> Statements { get; init; } = [];
    public ResultCounts Counts { get; init; } = new(0, 0, 0, 0);
    public ResultTotals Totals { get; init; } = new(0m, 0m);
    public IReadOnlyList<HeldItem> Held { get; init; } = [];
    public IReadOnlyList<SkippedItem> Skipped { get; init; } = [];
    public IReadOnlyList<PostedItem> Posted { get; init; } = [];
    public IReadOnlyList<UnreadableFile> Unreadable { get; init; } = [];

    /// <summary>Addition to spec §6 (like <see cref="Unreadable"/>): <c>result.json.unmatchedInvoices</c>, so an operator sees them here.</summary>
    public IReadOnlyList<InvoiceSummary> UnmatchedInvoices { get; init; } = [];

    public string? BatchId { get; init; }
    public string? Error { get; init; }
    public DateTime UpdatedUtc { get; init; }

    public static JobView From(JobRecord job, ResultDocument? result)
    {
        // A result.json older than this job record belongs to an earlier run of the same folder. While posting,
        // the dry-run result is still the best description of what is being posted.
        var fresh = result is not null && result.FinishedUtc >= job.CreatedUtc;
        var current = fresh && job.Status is not (JobStatus.Queued or JobStatus.Analysing) ? result : null;

        return new JobView
        {
            JobId = job.JobId,
            Status = job.Status,
            DryRun = job.DryRun,
            Company = current?.Company,
            Folder = job.Folder,
            Statements = current?.Reconcile.Select(s => new StatementView(s.File, s.Last4, s.Kind, s.Rows, s.Reconcile, s.HoldReason)).ToList() ?? [],
            Counts = current?.Counts ?? new ResultCounts(0, 0, 0, 0),
            Totals = current?.Totals ?? new ResultTotals(0m, 0m),
            Held = current?.Held ?? [],
            Skipped = current?.Skipped ?? [],
            Posted = current?.Posted ?? [],
            Unreadable = current?.Unreadable ?? [],
            UnmatchedInvoices = current?.UnmatchedInvoices ?? [],
            BatchId = job.BatchId,
            Error = job.Error,
            UpdatedUtc = job.UpdatedUtc,
        };
    }
}

public sealed record StatementView(string File, string? Last4, SourceKind? Kind, int Rows, ReconcileResult? Reconcile, string? HoldReason);

/// <summary><c>GET /jobs</c> item.</summary>
public sealed record JobSummary(string JobId, JobStatus Status, bool DryRun, string Folder, string? BatchId, DateTime UpdatedUtc, string? Error)
{
    public static JobSummary From(JobRecord j) => new(j.JobId, j.Status, j.DryRun, j.Folder, j.BatchId, j.UpdatedUtc, j.Error);
}
