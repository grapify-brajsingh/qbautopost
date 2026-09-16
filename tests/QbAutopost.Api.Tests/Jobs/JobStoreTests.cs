using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Tests.Jobs;

public sealed class JobStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FixedClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private JobStore NewStore()
    {
        var settings = new AppSettings { Paths = { JobIndex = _dir.Combine("jobs.json") } };
        return new JobStore(Options.Create(settings), _clock, NullLogger<JobStore>.Instance);
    }

    private JobRecord Queued(string jobId = "2026-08-test") => new()
    {
        JobId = jobId,
        Folder = _dir.Combine(jobId),
        Status = JobStatus.Queued,
        DryRun = true,
        CreatedUtc = _clock.UtcNow,
    };

    [Fact]
    public void Should_WriteStatusFileWithLowercaseStatus_When_Saved()
    {
        var store = NewStore();

        var saved = store.Save(Queued());

        using var doc = JsonDocument.Parse(File.ReadAllText(saved.StatusFile));
        Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("attempt").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("updatedUtc", out _));
    }

    [Fact]
    public void Should_StampUpdatedUtc_When_Saved()
    {
        var store = NewStore();
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        var saved = store.Save(Queued());

        Assert.Equal(_clock.UtcNow, saved.UpdatedUtc);
        Assert.Equal(saved, store.Get("2026-08-test"));
    }

    [Fact]
    public void Should_FindJobIgnoringCase_When_Getting()
    {
        var store = NewStore();
        store.Save(Queued());

        Assert.NotNull(store.Get("2026-08-TEST"));
        Assert.Null(store.Get("other"));
    }

    [Fact]
    public void Should_ReloadJobs_When_NewStoreStarts()
    {
        var first = NewStore();
        var saved = first.Save(Queued());
        first.Save(saved.MoveTo(JobStatus.Analysing));

        var reloaded = NewStore().Get("2026-08-test");

        Assert.NotNull(reloaded);
        Assert.Equal(JobStatus.Analysing, reloaded.Status);
        Assert.Equal(saved.Folder, reloaded.Folder);
    }

    [Fact]
    public void Should_SkipJob_When_StatusFileIsMissingOnReload()
    {
        var first = NewStore();
        var saved = first.Save(Queued());
        File.Delete(saved.StatusFile);

        Assert.Empty(NewStore().All());
    }

    [Fact]
    public void Should_SkipJob_When_StatusFileIsCorruptOnReload()
    {
        var first = NewStore();
        var saved = first.Save(Queued());
        File.WriteAllText(saved.StatusFile, "{ not json");

        Assert.Empty(NewStore().All());
    }

    [Fact]
    public void Should_ListNewestFirst_When_AllIsCalled()
    {
        var store = NewStore();
        store.Save(Queued("a-job"));
        store.Save(Queued("b-job") with { CreatedUtc = _clock.UtcNow.AddHours(1) });

        Assert.Equal(["b-job", "a-job"], store.All().Select(j => j.JobId));
    }
}
