using QbAutopost.Api.Jobs;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Output;

namespace QbAutopost.Api.Endpoints;

/// <summary>Job routes (spec §6). Errors are RFC 7807 problem details.</summary>
public static class JobEndpoints
{
    public sealed record CreateJobRequest(string? Folder, bool? DryRun, bool? Force);

    public sealed record CreateJobResponse(string JobId, JobStatus Status);

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/jobs");
        jobs.MapPost("/", CreateJob);
        jobs.MapGet("/", ListJobs);
        jobs.MapGet("/{id}", GetJob);
        jobs.MapPost("/{id}/post", PostJob);
        return app;
    }

    private static IResult CreateJob(CreateJobRequest? request, JobAdmission admission, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger(typeof(JobEndpoints));
        Admission result;
        try
        {
            result = admission.Create(request?.Folder, request?.DryRun, request?.Force ?? false);
        }
        catch (InvalidJobFolderException ex)
        {
            result = new Admission(AdmissionOutcome.Invalid, null, ex.Errors);
        }

        if (result.Outcome != AdmissionOutcome.Accepted)
        {
            log.LogWarning(
                "Job request for {Folder} refused ({Outcome}): {Errors}", request?.Folder, result.Outcome, string.Join("; ", result.Errors ?? []));
            return Problem(result);
        }

        log.LogInformation(
            "Job {JobId}: accepted from {Folder} (dryRun {DryRun}, force {Force}); queued",
            result.Job!.JobId, result.Job.Folder, result.Job.DryRun, request?.Force ?? false);
        return Results.Accepted($"/jobs/{result.Job.JobId}", new CreateJobResponse(result.Job.JobId, result.Job.Status));
    }

    private static IResult ListJobs(string? status, IJobStore store)
    {
        JobStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<JobStatus>(status, ignoreCase: true, out var parsed) || int.TryParse(status, out _))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid status filter",
                    detail: $"'{status}' is not a job status.");
            }

            filter = parsed;
        }

        return Results.Ok(store.All()
            .Where(j => filter is null || j.Status == filter)
            .Select(JobSummary.From)
            .ToList());
    }

    private static IResult GetJob(string id, IJobStore store)
    {
        var job = store.Get(id);
        return job is null
            ? Problem(Admission.Rejected(AdmissionOutcome.NotFound, $"job {id} is unknown"))
            : Results.Ok(JobView.From(job, JobOutputWriter.ReadResult(Path.Combine(job.Folder, FolderReader.OutputDirName))));
    }

    private static IResult PostJob(string id, JobAdmission admission, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger(typeof(JobEndpoints));
        var result = admission.RequestPost(id);
        if (result.Outcome != AdmissionOutcome.Accepted)
        {
            log.LogWarning("Job {JobId}: post refused ({Outcome}): {Errors}", id, result.Outcome, string.Join("; ", result.Errors ?? []));
            return Problem(result);
        }

        log.LogInformation("Job {JobId}: post requested; batch {BatchId} queued", result.Job!.JobId, result.Job.BatchId);
        return Results.Accepted($"/jobs/{result.Job.JobId}", new CreateJobResponse(result.Job.JobId, result.Job.Status));
    }

    private static IResult Problem(Admission result)
    {
        var (status, title) = result.Outcome switch
        {
            AdmissionOutcome.Invalid => (StatusCodes.Status400BadRequest, "Invalid job folder"),
            AdmissionOutcome.NotFound => (StatusCodes.Status404NotFound, "Job not found"),
            _ => (StatusCodes.Status409Conflict, "Job state conflict"),
        };
        var errors = result.Errors ?? [];
        return Results.Problem(
            statusCode: status,
            title: title,
            detail: string.Join("; ", errors),
            extensions: new Dictionary<string, object?> { ["errors"] = errors });
    }
}
