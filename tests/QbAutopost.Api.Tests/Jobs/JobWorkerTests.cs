using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Tests.Jobs;

public sealed class JobWorkerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly JobQueue _queue = new();
    private readonly JobStore _store;

    public JobWorkerTests()
    {
        var settings = new AppSettings { Paths = { JobIndex = _dir.Combine("jobs.json") } };
        _store = new JobStore(Options.Create(settings), new FixedClock(), NullLogger<JobStore>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    private void Save(string jobId, JobStatus status) =>
        _store.Save(new JobRecord { JobId = jobId, Folder = _dir.Combine(jobId), Status = status });

    private async Task RunUntil(RecordingProcessor processor, Func<bool> done)
    {
        using var worker = new JobWorker(_queue, processor, _store, NullLogger<JobWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_ProcessItemsInQueueOrder_When_Enqueued()
    {
        var processor = new RecordingProcessor();
        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));
        _queue.Enqueue(new JobWorkItem("b", JobAction.Post));

        await RunUntil(processor, () => processor.Seen.Count == 2);

        Assert.Equal(["a:Analyse", "b:Post"], processor.Seen);
        Assert.Equal(1, processor.MaxConcurrent);
    }

    [Fact]
    public async Task Should_FailJob_When_ProcessorThrowsDuringAnalysis()
    {
        Save("a", JobStatus.Analysing);
        var processor = new RecordingProcessor { ThrowFor = "a" };
        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));

        await RunUntil(processor, () => _store.Get("a")!.Status != JobStatus.Analysing);

        var job = _store.Get("a")!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("boom", job.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_MarkPartial_When_ProcessorThrowsDuringPosting()
    {
        Save("a", JobStatus.Posting);
        var processor = new RecordingProcessor { ThrowFor = "a" };
        _queue.Enqueue(new JobWorkItem("a", JobAction.Post));

        await RunUntil(processor, () => _store.Get("a")!.Status != JobStatus.Posting);

        var job = _store.Get("a")!;
        Assert.Equal(JobStatus.Partial, job.Status);
        Assert.StartsWith(JobWorker.InterruptedPosting, job.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_KeepRunning_When_OneItemThrows()
    {
        Save("a", JobStatus.Analysing);
        var processor = new RecordingProcessor { ThrowFor = "a" };
        _queue.Enqueue(new JobWorkItem("a", JobAction.Analyse));
        _queue.Enqueue(new JobWorkItem("b", JobAction.Analyse));

        await RunUntil(processor, () => processor.Seen.Count == 2);

        Assert.Equal(["a:Analyse", "b:Analyse"], processor.Seen);
    }

    private sealed class RecordingProcessor : IJobProcessor
    {
        private readonly object _gate = new();
        private readonly List<string> _seen = [];
        private int _running;

        public string? ThrowFor { get; init; }

        public int MaxConcurrent { get; private set; }

        public IReadOnlyList<string> Seen
        {
            get
            {
                lock (_gate)
                {
                    return _seen.ToList();
                }
            }
        }

        public async Task ProcessAsync(JobWorkItem item, CancellationToken ct)
        {
            var running = Interlocked.Increment(ref _running);
            MaxConcurrent = Math.Max(MaxConcurrent, running);
            await Task.Delay(10, ct);
            Interlocked.Decrement(ref _running);
            lock (_gate)
            {
                _seen.Add($"{item.JobId}:{item.Action}");
            }

            if (item.JobId == ThrowFor)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }
}
