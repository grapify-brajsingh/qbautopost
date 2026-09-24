using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Tests.Jobs;

/// <summary>
/// T-921 (ADR-0008): <see cref="JobQueue.TryRunExclusiveAsync"/> — the worker lock taken only if it is free.
/// <para>
/// The property that matters is not "a busy queue answers false"; it is that <b>nothing is enqueued</b> when it
/// does. A refusal that quietly left the work in the channel would post the batch minutes after the caller was told
/// nothing had been sent, which is worse than either answer on its own.
/// </para>
/// </summary>
public sealed class JobQueueTryExclusiveTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly JobQueue _queue = new();
    private readonly JobStore _store;

    public JobQueueTryExclusiveTests()
    {
        var settings = new AppSettings { Paths = { JobIndex = _dir.Combine("jobs.json") } };
        _store = new JobStore(Options.Create(settings), new FixedClock(), NullLogger<JobStore>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Should_RunTheWork_When_TheWorkerIsIdle()
    {
        using var worker = await StartWorkerAsync();

        var (ran, result) = await _queue.TryRunExclusiveAsync("post", _ => Task.FromResult(42), CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Should_RefuseWithoutEnqueuing_When_TheWorkerIsAlreadyBusy()
    {
        using var worker = await StartWorkerAsync();
        var started = Gate();
        var release = Gate();
        var first = _queue.TryRunExclusiveAsync(
            "first",
            async _ =>
            {
                started.SetResult();
                await release.Task;
                return 1;
            },
            CancellationToken.None);
        await started.Task;

        var second = await _queue.TryRunExclusiveAsync("second", _ => Task.FromResult(2), CancellationToken.None);

        Assert.False(second.Ran);
        Assert.Equal(0, second.Result);
        // Nothing was queued: the refused work must not run later, once the first call lets go.
        Assert.Equal(0, _queue.Depth);
        release.SetResult();
        Assert.Equal((true, 1), await first);
    }

    /// <summary>
    /// The idle check and the claim are one interlocked step, so of many callers arriving together exactly one is
    /// admitted. A check followed by an enqueue would let two through, and both would post believing they held the
    /// only QuickBooks session (ADR-0006).
    /// </summary>
    [Fact]
    public async Task Should_AdmitExactlyOne_When_ManyCallersArriveTogether()
    {
        using var worker = await StartWorkerAsync();
        var release = Gate();
        var inside = 0;
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _queue.TryRunExclusiveAsync(
                "post",
                async _ =>
                {
                    Interlocked.Increment(ref inside);
                    await release.Task;
                    return 1;
                },
                CancellationToken.None)))
            .ToList();

        // Every caller but one is refused at once, and the winner is inside the work holding the lock. Both halves
        // are waited for: the refusals are synchronous, but the winner's work only starts when the worker reaches
        // it, which is a moment later.
        await UntilAsync(() =>
            attempts.Count(a => a.IsCompleted) == attempts.Count - 1 && Volatile.Read(ref inside) == 1);

        Assert.Equal(1, Volatile.Read(ref inside));
        Assert.Equal(0, _queue.Depth);
        release.SetResult();
        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(r => r.Ran));
    }

    [Fact]
    public async Task Should_RefuseWithoutEnqueuing_When_AFolderJobIsWaiting()
    {
        // A job the worker has not reached yet still owns the lock's future: admitting a post here would be a queue.
        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));

        var (ran, _) = await _queue.TryRunExclusiveAsync("post", _ => Task.FromResult(1), CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(1, _queue.Depth);
    }

    [Fact]
    public async Task Should_BeFreeAgain_When_TheEarlierWorkHasFinished()
    {
        using var worker = await StartWorkerAsync();
        await _queue.TryRunExclusiveAsync("first", _ => Task.FromResult(1), CancellationToken.None);

        var (ran, result) = await _queue.TryRunExclusiveAsync("second", _ => Task.FromResult(2), CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(2, result);
        Assert.Equal(0, _queue.Outstanding);
    }

    /// <summary>
    /// The race the first version of this had, and the reason the claim is released before the result is published.
    /// A caller who posts twice in a row resumes from the first call while its work is still technically running on
    /// the worker, so a lock released only when the worker moved on would answer "busy" to the very caller that had
    /// just been told "posted". Twenty in a row, because once was not enough to see it.
    /// </summary>
    [Fact]
    public async Task Should_NeverAnswerBusy_When_OneCallerRunsOneAfterAnother()
    {
        using var worker = await StartWorkerAsync();

        for (var i = 0; i < 20; i++)
        {
            var (ran, result) = await _queue.TryRunExclusiveAsync("post", _ => Task.FromResult(i), CancellationToken.None);

            Assert.True(ran, $"attempt {i} was refused although nothing else was using the worker");
            Assert.Equal(i, result);
        }
    }

    [Fact]
    public async Task Should_BeFreeAgain_When_TheWorkThrows()
    {
        // The exception reaches the caller, but the lock must not stay held: every later caller would get 409.
        using var worker = await StartWorkerAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _queue.TryRunExclusiveAsync<int>(
            "first", _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        var (ran, _) = await _queue.TryRunExclusiveAsync("second", _ => Task.FromResult(2), CancellationToken.None);
        Assert.True(ran);
    }

    /// <summary>
    /// A gate whose continuations never run inline. Without this the worker thread that completes the gate would go
    /// on to run the rest of the test method, and the test would then be waiting for the thread it is standing on.
    /// </summary>
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(done(), "the expected state was not reached within 10 seconds");
    }

    private async Task<JobWorker> StartWorkerAsync()
    {
        var worker = new JobWorker(_queue, new IdleProcessor(), _store, NullLogger<JobWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        return worker;
    }

    private sealed class IdleProcessor : IJobProcessor
    {
        public Task ProcessAsync(JobWorkItem item, CancellationToken ct) => Task.CompletedTask;
    }
}
