using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Output;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Endpoints;

/// <summary>Job routes (spec §6, api-v1 §3). Errors are RFC 7807 problem details.</summary>
public static class JobEndpoints
{
    public sealed record CreateJobRequest(string? Folder, bool? DryRun, bool? Force);

    public sealed record CreateJobResponse(string JobId, JobStatus Status);

    /// <summary>FR-A-7 request: the folder to check, and nothing else — this route decides nothing about a job.</summary>
    public sealed record ValidateFolderRequest(string? Folder);

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/jobs");
        jobs.MapPost("/", CreateJob);
        jobs.MapGet("/", ListJobs);
        jobs.MapPost("/validate", ValidateFolder);
        jobs.MapGet("/{id}", GetJob);
        jobs.MapPost("/{id}/post", PostJob);
        return app;
    }

    /// <summary>
    /// FR-A-7. "Would this folder run?" — F1, the statement parse and G1, with no job queued and nothing written
    /// into the folder. 200 when it would run, 422 with the same body when it would not: a caller who only learns
    /// the answer on success learns nothing they can act on.
    /// <para>
    /// A missing <c>folder</c> is a malformed request (400), and a folder outside <c>Paths:AllowedJobRoots</c> is
    /// refused the same way <c>POST /jobs</c> refuses it (400) — this route must not become a way to ask what
    /// exists on the server (FR-A-17).
    /// </para>
    /// <para>
    /// SPEC-GAP T-914 (Q-68): <c>POST /jobs</c> answers <b>400</b> for the same F1 errors this route answers
    /// <b>422</b> for. FR-A-7 names only 200/422, so the validate follows its own FR and <c>POST /jobs</c> is left
    /// alone.
    /// </para>
    /// </summary>
    private static async Task<IResult> ValidateFolder(
        ValidateFolderRequest? request,
        IOptions<AppSettings> settings,
        FolderValidator validator,
        PipelineOptions pipeline,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(JobEndpoints));
        if (string.IsNullOrWhiteSpace(request?.Folder))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A folder is required",
                detail: "Send { \"folder\": \"<absolute path>\" } (api-v1 FR-A-7).");
        }

        if (!PathAllowList.IsInside(request.Folder, [.. settings.Value.Paths.AllowedJobRoots]))
        {
            log.LogWarning("Folder validation refused: the folder named is outside Paths:AllowedJobRoots");
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Folder is not in an allowed folder",
                detail: $"folder '{request.Folder}' is outside the folders this server accepts jobs from (Paths:AllowedJobRoots).");
        }

        var result = await validator.ValidateAsync(request.Folder, Rules.Load(pipeline.RulesFile), ct);
        log.LogInformation(
            "Folder {Folder} validated: {Ok}, {Statements} statement(s), {Errors} error(s)",
            request.Folder, result.Ok ? "would run" : "would not run", result.Statements.Count, result.Errors.Count);

        return result.Ok
            ? Results.Ok(result)
            : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
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
