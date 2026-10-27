using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Security;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Jobs;

public enum AdmissionOutcome
{
    Accepted,
    Invalid,
    NotFound,
    Conflict,
}

public sealed record Admission(AdmissionOutcome Outcome, JobRecord? Job = null, IReadOnlyList<string>? Errors = null)
{
    public static Admission Rejected(AdmissionOutcome outcome, params string[] errors) => new(outcome, null, errors);
}

/// <summary>
/// Decides whether a job may be created or posted (spec F1, F3, §6) and enqueues it. Check-and-save is serialised so
/// two concurrent requests for the same folder cannot both be accepted.
/// </summary>
public sealed class JobAdmission(
    IJobStore store, JobQueue queue, JobPipeline pipeline, IClock clock, IOptions<AppSettings> settings)
{
    private readonly object _gate = new();

    public Admission Create(string? folder, bool? dryRun, bool force)
    {
        var errors = FolderReader.Validate(folder);
        if (errors.Count > 0)
        {
            return new Admission(AdmissionOutcome.Invalid, null, errors);
        }

        // T-911 (FR-A-17): with remote callers (D-4) the folder is a stranger's string, so it must sit where the
        // owner said jobs live. Checked after Validate so a caller still learns "does not exist" before "not
        // allowed" — the likelier mistake, and an answer that reveals nothing they had not already named.
        if (!PathAllowList.IsInside(folder, [.. settings.Value.Paths.AllowedJobRoots]))
        {
            return Admission.Rejected(
                AdmissionOutcome.Invalid,
                $"folder '{folder}' is outside the folders this server accepts jobs from (Paths:AllowedJobRoots)");
        }

        var jobId = FolderReader.JobIdOf(folder!);
        lock (_gate)
        {
            var existing = store.Get(jobId);
            if (existing is not null && JobStatusRules.IsActive(existing.Status))
            {
                return Admission.Rejected(AdmissionOutcome.Conflict, $"job {jobId} is {existing.Status.ToString().ToLowerInvariant()}");
            }

            if (!force && new LedgerStore(settings.Value.Paths.Ledger).Load().HasJob(jobId))
            {
                return Admission.Rejected(
                    AdmissionOutcome.Conflict, $"job {jobId} is already in the ledger; send force=true to run it again");
            }

            var input = FolderReader.Read(folder!);
            var now = clock.UtcNow;
            var saved = store.Save(new JobRecord
            {
                JobId = input.JobId,
                Folder = input.Folder,
                Status = JobStatus.Queued,
                DryRun = dryRun ?? settings.Value.DryRunDefault,
                CreatedUtc = now,
            });
            queue.Enqueue(new JobWorkItem(saved.JobId, JobAction.Analyse));
            return new Admission(AdmissionOutcome.Accepted, saved);
        }
    }

    public Admission RequestPost(string jobId)
    {
        lock (_gate)
        {
            var job = store.Get(jobId);
            if (job is null)
            {
                return Admission.Rejected(AdmissionOutcome.NotFound, $"job {jobId} is unknown");
            }

            if (job.Status != JobStatus.Ready)
            {
                return Admission.Rejected(
                    AdmissionOutcome.Conflict, $"job {jobId} is {job.Status.ToString().ToLowerInvariant()}; only a ready job can be posted");
            }

            var posting = store.Save(pipeline.BeginPosting(job));
            queue.Enqueue(new JobWorkItem(posting.JobId, JobAction.Post));
            return new Admission(AdmissionOutcome.Accepted, posting);
        }
    }
}
