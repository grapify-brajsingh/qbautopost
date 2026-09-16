namespace QbAutopost.Api.Jobs;

/// <summary>Runs one queued work item, owning the job's status transitions.</summary>
public interface IJobProcessor
{
    Task ProcessAsync(JobWorkItem item, CancellationToken ct);
}
