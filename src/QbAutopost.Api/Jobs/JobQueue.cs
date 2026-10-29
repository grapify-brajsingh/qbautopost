using System.Runtime.CompilerServices;
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
public sealed record JobWorkItem(string JobId, JobAction Action, Func<CancellationToken, Task>? Work = null)
{
    /// <summary>
    /// True when <see cref="Work"/> releases the queue's busy claim itself, just before it hands the result back to
    /// whoever is waiting — see <see cref="JobQueue.TryRunExclusiveAsync"/>. The queue then does not release it a
    /// second time when the worker moves on.
    /// </summary>
    internal bool ReleasesItself { get; init; }
}

/// <summary>In-process FIFO for the single <see cref="JobWorker"/> (ADR-0003). Not durable: startup recovery covers a restart.</summary>
public sealed class JobQueue
{
    private readonly Channel<JobWorkItem> _channel =
        Channel.CreateUnbounded<JobWorkItem>(new UnboundedChannelOptions { SingleReader = true });

    private int _depth;

    private int _outstanding;

    /// <summary>
    /// Items waiting for the worker, not counting the one it is running (FR-A-1). Counted here rather than through
    /// <c>ChannelReader.Count</c>, which a single-reader unbounded channel does not support.
    /// </summary>
    public int Depth => Volatile.Read(ref _depth);

    /// <summary>
    /// Items queued <b>or running</b> — <see cref="Depth"/> plus the one the worker owns. Counted separately
    /// because <see cref="TryRunExclusiveAsync"/> asks a different question from FR-A-1's queue depth: a caller who
    /// must not wait needs to know about the item already running, which <see cref="Depth"/> deliberately excludes.
    /// </summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    public void Enqueue(JobWorkItem item)
    {
        Interlocked.Increment(ref _outstanding);
        try
        {
            Write(item);
        }
        catch
        {
            Interlocked.Decrement(ref _outstanding);
            throw;
        }
    }

    public async IAsyncEnumerable<JobWorkItem> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            // Decremented as the worker takes the item: from here it is running, not waiting.
            Interlocked.Decrement(ref _depth);
            try
            {
                yield return item;
            }
            finally
            {
                // The worker has finished this item — the body of its foreach has returned — so it stops counting
                // as work in flight. In a finally, so an abandoned enumeration cannot leave the worker looking busy
                // for ever, which would answer 409 to every caller after it.
                //
                // Exclusive work has already released itself by now (see Wrap): it must, because it hands its
                // result to a waiting caller from *inside* the work, and that caller may immediately ask for the
                // lock again. Releasing only here would leave a window in which a caller who has just been told
                // "posted" is told "busy" by the very post they were waiting for.
                if (!item.ReleasesItself)
                {
                    Interlocked.Decrement(ref _outstanding);
                }
            }
        }
    }

    /// <summary>
    /// M14 §5: the worker's bounded wait. Runs the next item through <paramref name="run"/> — with the same
    /// <see cref="Depth"/>/<see cref="Outstanding"/> accounting as <see cref="ReadAllAsync"/> — and returns true; or
    /// returns false when <paramref name="deadline"/> completes first and nothing is waiting. A null deadline means
    /// "do not wait": only an item already in the channel is run.
    /// <para>
    /// An item that arrives together with the deadline still runs (a job always goes before the scheduled sync). The
    /// deadline is handed a token that is cancelled once the wait is over, and must honour it. A deadline that faults,
    /// or is cancelled by anything but this wait, throws <see cref="InvalidOperationException"/> (it is not "due").
    /// </para>
    /// </summary>
    public async Task<bool> RunNextAsync(
        Func<JobWorkItem, Task> run, Func<CancellationToken, Task>? deadline, CancellationToken ct)
    {
        if (!_channel.Reader.TryRead(out var item))
        {
            if (deadline is null)
            {
                return false;
            }

            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var ready = _channel.Reader.WaitToReadAsync(wait.Token).AsTask();
                var due = deadline(wait.Token);
                var first = await Task.WhenAny(ready, due);

                // A deadline that ended first without running to completion (faulted, or cancelled by something other
                // than this wait) is broken: it is not "due", and reporting it as such would sync on every pass.
                var broken = first == due && !due.IsCompletedSuccessfully && !ct.IsCancellationRequested;
                wait.Cancel();

                // The deadline's own faults are observed here; only the channel wait is awaited to its end.
                _ = due.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                try
                {
                    await ready;
                }
                catch (OperationCanceledException)
                {
                    // The deadline came first, or the host is stopping (checked below).
                }

                ct.ThrowIfCancellationRequested();
                if (broken)
                {
                    throw new InvalidOperationException("The wait for the scheduled list sync failed.", due.Exception?.GetBaseException());
                }
            }

            // Nothing waiting now: the caller runs the sync, and an item that arrives after this read waits behind it
            // (at most one sync, spec §5.7).
            if (!_channel.Reader.TryRead(out item))
            {
                return false;
            }
        }

        // As in ReadAllAsync: running, not waiting, from here; no longer in flight once run has returned.
        Interlocked.Decrement(ref _depth);
        try
        {
            await run(item);
        }
        finally
        {
            if (!item.ReleasesItself)
            {
                Interlocked.Decrement(ref _outstanding);
            }
        }

        return true;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the single worker, after any job ahead of it, and returns its result. The work
    /// runs to the end even if the caller stops waiting (<paramref name="ct"/> only ends the wait).
    /// </summary>
    public Task<T> RunExclusiveAsync<T>(string label, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(Wrap(label, work, result));
        return result.Task.WaitAsync(ct);
    }

    /// <summary>
    /// T-921 (ADR-0008): the same exclusive run, but only if the worker is free. When it is not, nothing is
    /// enqueued and <c>Ran</c> is false, so a caller can be refused at once instead of waiting in a line they can
    /// neither see nor cancel — which is what "not queued" means for the synchronous post.
    /// <para>
    /// The claim is one interlocked step, not a read followed by an enqueue. Two requests arriving together would
    /// both read "idle" and both be admitted, and each caller would believe they held the only QuickBooks session —
    /// the collision ADR-0006 exists to prevent. Exactly one wins the compare-and-exchange; the other is told so.
    /// </para>
    /// <para>
    /// The work still runs <b>on the worker</b>, so it is serialised with folder jobs in the ordinary way: a job
    /// enqueued a moment later waits behind it rather than beside it. <paramref name="ct"/> only bounds the wait.
    /// </para>
    /// </summary>
    public async Task<(bool Ran, T? Result)> TryRunExclusiveAsync<T>(
        string label, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _outstanding, 1, 0) != 0)
        {
            return (false, default);
        }

        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Write, not Enqueue: the claim above has already counted this item, and counting it twice would leave
            // the worker looking busy after it finished.
            Write(Wrap(label, work, result));
        }
        catch
        {
            Interlocked.Decrement(ref _outstanding);
            throw;
        }

        return (true, await result.Task.WaitAsync(ct));
    }

    /// <summary>
    /// The work item both exclusive paths run: the caller's result, or their exception, back on the wire.
    /// <para>
    /// The claim is released <b>before</b> the result is published, and that ordering is the fix for a real race
    /// rather than tidiness. The waiting caller resumes the moment the result is set — still inside this work, and
    /// so still inside the worker's <c>foreach</c> body — and a caller who posts twice in a row would have found
    /// the lock still held and been answered 409 by the post they had just been told had succeeded. Releasing
    /// early is safe: a claim made in that instant only puts an item in the channel, which the single worker cannot
    /// reach until it has finished this one.
    /// </para>
    /// </summary>
    private JobWorkItem Wrap<T>(string label, Func<CancellationToken, Task<T>> work, TaskCompletionSource<T> result) =>
        new(label, JobAction.Exclusive, async workerCt =>
        {
            try
            {
                var value = await work(workerCt);
                Interlocked.Decrement(ref _outstanding);
                result.TrySetResult(value);
            }
            catch (OperationCanceledException) when (workerCt.IsCancellationRequested)
            {
                Interlocked.Decrement(ref _outstanding);
                result.TrySetCanceled(workerCt);
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _outstanding);
                result.TrySetException(ex);
            }
        })
        { ReleasesItself = true };

    private void Write(JobWorkItem item)
    {
        if (!_channel.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("The job queue is closed.");
        }

        Interlocked.Increment(ref _depth);
    }
}
