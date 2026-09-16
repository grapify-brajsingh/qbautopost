using System.Threading.Channels;

namespace QbAutopost.Api.Jobs;

public enum JobAction
{
    /// <summary>FR-1…FR-9, then post straight away when the job is not a dry run (FR-10).</summary>
    Analyse,

    /// <summary><c>POST /jobs/{id}/post</c>: re-run mapping and post (FR-10).</summary>
    Post,

    /// <summary>
    /// A self-contained operation that must not overlap a job (undo, sync-lists): it touches QuickBooks and the shared
    /// ledger/list files. The worker runs <see cref="JobWorkItem.Work"/> itself; errors go back to the waiting caller.
    /// </summary>
    Exclusive,
}

/// <summary>One unit of work. <paramref name="JobId"/> is the log correlation id (for exclusive work: a label).</summary>
public sealed record JobWorkItem(string JobId, JobAction Action, Func<CancellationToken, Task>? Work = null);

/// <summary>In-process FIFO for the single <see cref="JobWorker"/> (ADR-0003). Not durable: startup recovery covers a restart.</summary>
public sealed class JobQueue
{
    private readonly Channel<JobWorkItem> _channel =
        Channel.CreateUnbounded<JobWorkItem>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(JobWorkItem item)
    {
        if (!_channel.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("The job queue is closed.");
        }
    }

    public IAsyncEnumerable<JobWorkItem> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Runs <paramref name="work"/> on the single worker, after any job ahead of it, and returns its result. The work
    /// runs to the end even if the caller stops waiting (<paramref name="ct"/> only ends the wait).
    /// </summary>
    public Task<T> RunExclusiveAsync<T>(string label, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new JobWorkItem(label, JobAction.Exclusive, async workerCt =>
        {
            try
            {
                result.TrySetResult(await work(workerCt));
            }
            catch (OperationCanceledException) when (workerCt.IsCancellationRequested)
            {
                result.TrySetCanceled(workerCt);
                throw;
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        }));
        return result.Task.WaitAsync(ct);
    }
}
