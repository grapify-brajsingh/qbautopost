using System.Net.Http.Json;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Jobs;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>Startup recovery of interrupted jobs (spec §6, plan T-106).</summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>Writes what a crashed host would have left behind: the job index and one status.json.</summary>
    private void SeedJob(string jobId, JobStatus status)
    {
        var folder = _factory.Dir.CopySampleJob(jobId);
        var record = new JobRecord
        {
            JobId = jobId,
            Folder = folder,
            Status = status,
            DryRun = true,
            Attempt = status == JobStatus.Posting ? 1 : 0,
            BatchId = status == JobStatus.Posting ? $"{jobId}#1" : null,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-5),
        };
        AtomicFile.WriteJson(record.StatusFile, record);

        var index = File.Exists(_factory.JobIndexFile) ? File.ReadAllText(_factory.JobIndexFile) : null;
        var entries = index is null ? [] : System.Text.Json.JsonDocument.Parse(index).RootElement.GetProperty("jobs")
            .EnumerateArray()
            .Select(e => new { jobId = e.GetProperty("jobId").GetString(), folder = e.GetProperty("folder").GetString() })
            .ToList();
        entries.Add(new { jobId = (string?)jobId, folder = (string?)folder });
        AtomicFile.WriteJson(_factory.JobIndexFile, new { jobs = entries });
    }

    [Fact]
    public async Task Should_FailJob_When_HostStartsWithJobAnalysing()
    {
        SeedJob("2026-08-analysing", JobStatus.Analysing);
        using var client = _factory.CreateAuthorizedClient();

        var view = await client.GetJobAsync("2026-08-analysing");

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.Equal(StartupRecovery.InterruptedAnalysis, view.Error);
    }

    [Fact]
    public async Task Should_MarkPartial_When_HostStartsWithJobPosting()
    {
        SeedJob("2026-08-posting", JobStatus.Posting);
        using var client = _factory.CreateAuthorizedClient();

        var view = await client.GetJobAsync("2026-08-posting");

        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.Equal(JobWorker.InterruptedPosting, view.Error);
        Assert.Equal("2026-08-posting#1", view.BatchId);
    }

    [Fact]
    public async Task Should_FailJob_When_HostStartsWithJobQueued()
    {
        SeedJob("2026-08-queued", JobStatus.Queued);
        using var client = _factory.CreateAuthorizedClient();

        var view = await client.GetJobAsync("2026-08-queued");

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.Equal(StartupRecovery.InterruptedQueued, view.Error);
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_KeepFinishedJobs_When_HostStarts()
    {
        SeedJob("2026-08-ready", JobStatus.Ready);
        using var client = _factory.CreateAuthorizedClient();

        var view = await client.GetJobAsync("2026-08-ready");

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Null(view.Error);
    }

    [Fact]
    public async Task Should_PersistRecoveredStatus_When_HostStarts()
    {
        SeedJob("2026-08-analysing", JobStatus.Analysing);
        using var client = _factory.CreateAuthorizedClient();
        await client.GetJobAsync("2026-08-analysing");

        var statusFile = JobRecord.StatusFileOf(_factory.Dir.Combine("2026-08-analysing"));

        Assert.Contains("\"failed\"", File.ReadAllText(statusFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_AllowResubmission_When_RecoveredJobFailed()
    {
        SeedJob("2026-08-analysing", JobStatus.Analysing);
        using var client = _factory.CreateAuthorizedClient();

        var view = await client.RunToEndAsync(_factory.Dir.Combine("2026-08-analysing"));

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Null(view.Error);
    }

    [Fact]
    public async Task Should_ListKnownJobsFromStatusFiles_When_HostStarts()
    {
        SeedJob("2026-08-ready", JobStatus.Ready);
        SeedJob("2026-08-posted", JobStatus.Posted);
        using var client = _factory.CreateAuthorizedClient();

        var all = await client.GetFromJsonAsync<List<JobSummary>>("/jobs", ApiFactory.Json);
        var posted = await client.GetFromJsonAsync<List<JobSummary>>("/jobs?status=posted", ApiFactory.Json);

        Assert.Equal(["2026-08-posted", "2026-08-ready"], all!.Select(j => j.JobId).Order(StringComparer.Ordinal));
        Assert.Equal("2026-08-posted", Assert.Single(posted!).JobId);
    }

    [Fact]
    public async Task Should_ListRecoveredStatus_When_FilteringAfterRestart()
    {
        SeedJob("2026-08-posting", JobStatus.Posting);
        using var client = _factory.CreateAuthorizedClient();

        var partial = await client.GetFromJsonAsync<List<JobSummary>>("/jobs?status=partial", ApiFactory.Json);
        var posting = await client.GetFromJsonAsync<List<JobSummary>>("/jobs?status=posting", ApiFactory.Json);

        Assert.Equal(JobWorker.InterruptedPosting, Assert.Single(partial!).Error);
        Assert.Empty(posting!);
    }
}
