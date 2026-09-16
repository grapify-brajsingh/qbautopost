using System.Threading.Channels;

namespace QbAutopost.Api.Jobs;

public enum JobAction
{
    /// <summary>FR-1…FR-9, then post straight away when the job is not a dry run (FR-10).</summary>
    Analyse,

    /// <summary><c>POST /jobs/{id}/post</c>: re-run mapping and post (FR-10).</summary>
    Post,
}

public sealed record JobWorkItem(string JobId, JobAction Action);

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
}
