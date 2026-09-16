using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Jobs;

/// <summary>
/// Spec §6 startup recovery, run once before the worker starts. The in-memory queue is gone after a restart, so no
/// job may stay in an active state. status.json is the authority afterwards; result.json keeps the last completed run.
/// </summary>
public sealed class StartupRecovery(IJobStore store, ILogger<StartupRecovery> log)
{
    public const string InterruptedAnalysis = "interrupted";

    // SPEC-GAP T-106: the spec names analysing and posting only; a queued job is failed too rather than re-run (Q-14).
    public const string InterruptedQueued = "interrupted (was queued when the host stopped); submit the folder again";

    /// <summary>Returns the number of jobs that were fixed.</summary>
    public int Run()
    {
        var fixedCount = 0;
        foreach (var job in store.All())
        {
            (JobStatus Status, string Error)? next = job.Status switch
            {
                JobStatus.Queued => (JobStatus.Failed, InterruptedQueued),
                JobStatus.Analysing => (JobStatus.Failed, InterruptedAnalysis),
                JobStatus.Posting => (JobStatus.Partial, JobWorker.InterruptedPosting),
                _ => null,
            };

            if (next is null)
            {
                continue;
            }

            store.Save(job.MoveTo(next.Value.Status, next.Value.Error));
            log.LogWarning("Job {JobId}: was {Old} at startup, now {New} ({Error})", job.JobId, job.Status, next.Value.Status, next.Value.Error);
            fixedCount++;
        }

        return fixedCount;
    }
}
