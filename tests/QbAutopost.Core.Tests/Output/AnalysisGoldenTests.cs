using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Output;

/// <summary>
/// analysis.json for the sample job against a reviewed golden file (plan T-105). analysis.json has no timestamps,
/// so it is deterministic. On a mismatch the actual file is saved next to the test assembly for review.
/// </summary>
public sealed class AnalysisGoldenTests : IDisposable
{
    private readonly TempJobFolder _job = TempJobFolder.FromSample();

    public void Dispose() => _job.Dispose();

    [Fact]
    public async Task Should_MatchGoldenFile_When_SampleJobIsAnalysed()
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
            TestStatementReader.Create(),
            null!,
            new SystemClock());
        var job = new JobRecord { JobId = "2026-08-tropicana", Folder = _job.Folder, Status = JobStatus.Analysing, DryRun = true };

        await pipeline.RunAnalysisAsync(job, CancellationToken.None);

        var actual = File.ReadAllText(_job.PathOf("output", "analysis.json")).ReplaceLineEndings("\n");
        var expected = File.Exists(Fixtures.PathOf("output", "sample-analysis.golden.json"))
            ? Fixtures.Read("output", "sample-analysis.golden.json").ReplaceLineEndings("\n")
            : "";
        if (actual != expected)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "sample-analysis.actual.json"), actual);
        }

        Assert.Equal(expected, actual);
    }
}
