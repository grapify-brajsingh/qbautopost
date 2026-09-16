using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Output;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Jobs;

/// <summary>
/// Drives one job through the spec §6 state machine, persisting every transition before the next step (CLAUDE.md rule 7).
/// </summary>
public sealed class JobRunner(IJobStore store, JobPipeline pipeline, IClock clock, ILogger<JobRunner> log) : IJobProcessor
{
    public Task ProcessAsync(JobWorkItem item, CancellationToken ct) => item.Action switch
    {
        JobAction.Analyse => AnalyseAsync(item.JobId, ct),
        JobAction.Post => PostAsync(item.JobId, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(item), item.Action, "Unknown job action."),
    };

    private async Task AnalyseAsync(string jobId, CancellationToken ct)
    {
        var job = store.Get(jobId);
        if (job is not { Status: JobStatus.Queued })
        {
            log.LogWarning("Job {JobId}: analyse skipped, status is {Status}", jobId, job?.Status);
            return;
        }

        var started = clock.UtcNow;
        job = store.Save(job.MoveTo(JobStatus.Analysing));

        AnalysisResult analysis;
        try
        {
            analysis = await pipeline.RunAnalysisAsync(job, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Job {JobId}: analysis failed", jobId);
            Finish(job.MoveTo(JobStatus.Failed, $"analysis failed: {ex.Message}"), null, null, started);
            return;
        }

        if (analysis.FailReason is { } reason)
        {
            Finish(job.MoveTo(JobStatus.Failed, reason), analysis, null, started);
            return;
        }

        if (job.DryRun)
        {
            Finish(job.MoveTo(JobStatus.Ready), analysis, null, started);
            return;
        }

        // FR-10 dryRun=false: post straight after FR-9 with this analysis.
        job = store.Save(pipeline.BeginPosting(job));
        await PostAndFinishAsync(job, analysis, started, ct);
    }

    private async Task PostAsync(string jobId, CancellationToken ct)
    {
        var job = store.Get(jobId);
        if (job is not { Status: JobStatus.Posting })
        {
            log.LogWarning("Job {JobId}: post skipped, status is {Status}", jobId, job?.Status);
            return;
        }

        await PostAndFinishAsync(job, null, clock.UtcNow, ct);
    }

    private async Task PostAndFinishAsync(JobRecord job, AnalysisResult? analysis, DateTime started, CancellationToken ct)
    {
        PostOutcome outcome;
        try
        {
            outcome = analysis is null
                ? await pipeline.RunPostAsync(job, ct)
                : await pipeline.PostAsync(job, analysis, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Job {JobId}: posting failed", job.JobId);
            Finish(job.MoveTo(JobStatus.Partial, $"{JobWorker.InterruptedPosting}: {ex.Message}"), analysis, null, started);
            return;
        }

        Finish(job.MoveTo(outcome.Status, outcome.Error), outcome.Analysis, outcome, started);
    }

    /// <summary>result.json first, then status.json: the status change is what tells clients the run is over.</summary>
    private void Finish(JobRecord job, AnalysisResult? analysis, PostOutcome? outcome, DateTime started)
    {
        try
        {
            var outputDir = Path.Combine(job.Folder, FolderReader.OutputDirName);
            JobOutputWriter.WriteResult(outputDir, ResultDocument.Build(job, analysis, outcome, started, clock.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Job {JobId}: result.json could not be written", job.JobId);
        }

        store.Save(job);
        log.LogInformation("Job {JobId}: {Status} {Error}", job.JobId, job.Status, job.Error);
    }
}
