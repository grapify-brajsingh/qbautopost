using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Jobs;

/// <summary>
/// The single background worker (ADR-0003): one job at a time, so QuickBooks calls and ledger writes are serialised.
/// </summary>
public sealed class JobWorker(JobQueue queue, IJobProcessor processor, IJobStore store, ILogger<JobWorker> log)
    : BackgroundService
{
    public const string InterruptedPosting = "interrupted — run duplicates before re-post";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queue.ReadAllAsync(stoppingToken))
            {
                await RunOneAsync(item, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping; a job left in analysing/posting is fixed by startup recovery.
        }
    }

    private async Task RunOneAsync(JobWorkItem item, CancellationToken stoppingToken)
    {
        using var scope = log.BeginScope(new Dictionary<string, object> { ["jobId"] = item.JobId });
        try
        {
            log.LogInformation("Job {JobId}: {Action} started", item.JobId, item.Action);
            if (item.Action == JobAction.Exclusive)
            {
                // Not a job: the work reports its own errors to the caller and never changes a job's status here.
                await item.Work!(stoppingToken);
                log.LogInformation("Job {JobId}: {Action} finished", item.JobId, item.Action);
                return;
            }

            await processor.ProcessAsync(item, stoppingToken);
            log.LogInformation("Job {JobId}: {Action} finished with status {Status}", item.JobId, item.Action, store.Get(item.JobId)?.Status);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Job {JobId}: {Action} crashed", item.JobId, item.Action);
            FailSafe(item.JobId, ex);
        }
    }

    /// <summary>Last resort when the processor itself throws: never leave a job looking active.</summary>
    private void FailSafe(string jobId, Exception ex)
    {
        var record = store.Get(jobId);
        var next = record?.Status switch
        {
            JobStatus.Queued or JobStatus.Analysing => JobStatus.Failed,
            JobStatus.Posting => JobStatus.Partial,
            _ => (JobStatus?)null,
        };

        if (record is null || next is null)
        {
            return;
        }

        var error = next == JobStatus.Partial
            ? $"{InterruptedPosting}: {ex.Message}"
            : $"internal error: {ex.Message}";
        store.Save(record.MoveTo(next.Value, error));
    }
}
