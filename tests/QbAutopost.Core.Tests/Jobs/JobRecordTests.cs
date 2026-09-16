using QbAutopost.Core.Jobs;

namespace QbAutopost.Core.Tests.Jobs;

public sealed class JobRecordTests
{
    private static JobRecord Record(JobStatus status) =>
        new() { JobId = "2026-08-test", Folder = "/jobs/2026-08-test", Status = status };

    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Analysing)]
    [InlineData(JobStatus.Analysing, JobStatus.Failed)]
    [InlineData(JobStatus.Analysing, JobStatus.Ready)]
    [InlineData(JobStatus.Analysing, JobStatus.Posting)]
    [InlineData(JobStatus.Ready, JobStatus.Posting)]
    [InlineData(JobStatus.Posting, JobStatus.Posted)]
    [InlineData(JobStatus.Posting, JobStatus.Partial)]
    [InlineData(JobStatus.Posted, JobStatus.Undone)]
    [InlineData(JobStatus.Partial, JobStatus.Undone)]
    public void Should_Move_When_SpecStateMachineAllowsIt(JobStatus from, JobStatus to)
    {
        var moved = Record(from).MoveTo(to, "why");

        Assert.Equal(to, moved.Status);
        Assert.Equal("why", moved.Error);
    }

    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Ready)]
    [InlineData(JobStatus.Ready, JobStatus.Posted)]
    [InlineData(JobStatus.Ready, JobStatus.Analysing)]
    [InlineData(JobStatus.Posting, JobStatus.Failed)]
    [InlineData(JobStatus.Failed, JobStatus.Posting)]
    [InlineData(JobStatus.Posted, JobStatus.Posting)]
    public void Should_Throw_When_SpecStateMachineForbidsTheMove(JobStatus from, JobStatus to)
    {
        Assert.Throws<InvalidOperationException>(() => Record(from).MoveTo(to));
    }

    [Theory]
    [InlineData(JobStatus.Queued, true)]
    [InlineData(JobStatus.Analysing, true)]
    [InlineData(JobStatus.Posting, true)]
    [InlineData(JobStatus.Ready, false)]
    [InlineData(JobStatus.Failed, false)]
    [InlineData(JobStatus.Partial, false)]
    public void Should_ReportActive_When_WorkerOwnsTheJob(JobStatus status, bool active)
    {
        Assert.Equal(active, JobStatusRules.IsActive(status));
    }

    [Fact]
    public void Should_PlaceStatusFileUnderOutput_When_Asked()
    {
        Assert.Equal(Path.Combine("/jobs/2026-08-test", "output", "status.json"), Record(JobStatus.Queued).StatusFile);
    }
}
