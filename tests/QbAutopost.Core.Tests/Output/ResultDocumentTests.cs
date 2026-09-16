using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Output;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Output;

/// <summary>result.json shape (spec §10) for a dry run of the sample job.</summary>
public sealed class ResultDocumentTests : IDisposable
{
    private static readonly DateTime Started = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

    private readonly TempJobFolder _job = TempJobFolder.FromSample();

    public void Dispose() => _job.Dispose();

    private async Task<(JobRecord Job, AnalysisResult Analysis)> DryRun()
    {
        var pipeline = new JobPipeline(
            new PipelineOptions
            {
                CompanyName = "Tropicana Properties LLC",
                RulesFile = Path.Combine(AppContext.BaseDirectory, "samples", "rules.json"),
                LedgerFile = Path.Combine(_job.Root, "ledger.json"),
                QbListsFile = Path.Combine(_job.Root, "qb-lists.json"),
            },
            new RegexSpecReader(),
            new DisabledOcr(),
            null!,
            new SystemClock());
        var job = new JobRecord { JobId = "2026-08-tropicana", Folder = _job.Folder, Status = JobStatus.Ready, DryRun = true };
        return (job, await pipeline.RunAnalysisAsync(job, CancellationToken.None));
    }

    [Fact]
    public async Task Should_CountAndTotalPostableLines_When_DryRunIsReady()
    {
        var (job, analysis) = await DryRun();

        var result = ResultDocument.Build(job, analysis, null, Started, Started.AddSeconds(3));

        Assert.Equal(new ResultCounts(8, 0, 2, 1), result.Counts);
        Assert.Equal(analysis.ToPost.Sum(t => t.Line.Amount), result.Totals.ToPost);
        Assert.Equal(0m, result.Totals.Posted);
        Assert.Equal(2, result.Reconcile.Count);
        Assert.All(result.Held, h => Assert.NotEmpty(h.Description));
    }

    [Fact]
    public async Task Should_RoundTripThroughResultFile_When_Written()
    {
        var (job, analysis) = await DryRun();
        var result = ResultDocument.Build(job, analysis, null, Started, Started.AddSeconds(3));

        JobOutputWriter.WriteResult(analysis.Input.OutputDir, result);
        var read = JobOutputWriter.ReadResult(analysis.Input.OutputDir);

        Assert.NotNull(read);
        Assert.Equal(result.Counts, read.Counts);
        Assert.Equal(result.Held.Select(h => h.RequestId), read.Held.Select(h => h.RequestId));
        Assert.Equal(result.Reconcile.Select(r => r.Reconcile), read.Reconcile.Select(r => r.Reconcile));
    }

    [Fact]
    public async Task Should_UseSpecFieldNames_When_Serialised()
    {
        var (job, analysis) = await DryRun();
        JobOutputWriter.WriteResult(analysis.Input.OutputDir, ResultDocument.Build(job, analysis, null, Started, Started));

        using var doc = JsonDocument.Parse(File.ReadAllText(_job.PathOf("output", "result.json")));
        var root = doc.RootElement;
        foreach (var name in new[] { "jobId", "batchId", "status", "dryRun", "counts", "totals", "posted", "held", "skipped", "unreadable", "unmatchedInvoices", "reconcile", "startedUtc", "finishedUtc" })
        {
            Assert.True(root.TryGetProperty(name, out _), name);
        }

        Assert.Equal("ready", root.GetProperty("status").GetString());
    }

    [Fact]
    public void Should_ReturnNull_When_ResultFileIsMissingOrCorrupt()
    {
        var dir = _job.PathOf("output");
        Directory.CreateDirectory(dir);
        Assert.Null(JobOutputWriter.ReadResult(dir));

        File.WriteAllText(Path.Combine(dir, JobOutputWriter.ResultFile), "{ nope");
        Assert.Null(JobOutputWriter.ReadResult(dir));
    }
}
