namespace QbAutopost.Core.Jobs;

/// <summary>Job state machine (spec §6).</summary>
public enum JobStatus
{
    Queued,
    Analysing,
    Ready,
    Posting,
    Posted,
    Partial,
    Failed,
    Undone,
}

public static class JobStatusRules
{
    private static readonly Dictionary<JobStatus, JobStatus[]> Allowed = new()
    {
        // SPEC-GAP T-102: queued → failed exists only for startup recovery of a job whose queue entry was lost.
        [JobStatus.Queued] = [JobStatus.Analysing, JobStatus.Failed],
        [JobStatus.Analysing] = [JobStatus.Failed, JobStatus.Ready, JobStatus.Posting],
        [JobStatus.Ready] = [JobStatus.Posting],
        [JobStatus.Posting] = [JobStatus.Posted, JobStatus.Partial],
        [JobStatus.Posted] = [JobStatus.Undone],
        [JobStatus.Partial] = [JobStatus.Undone],
        [JobStatus.Failed] = [],
        [JobStatus.Undone] = [],
    };

    public static bool CanMove(JobStatus from, JobStatus to) => Allowed[from].Contains(to);

    /// <summary>True while the worker owns the job; such a job cannot be replaced by a new <c>POST /jobs</c>.</summary>
    public static bool IsActive(JobStatus status) =>
        status is JobStatus.Queued or JobStatus.Analysing or JobStatus.Posting;
}
