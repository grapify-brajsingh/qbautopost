using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Api;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Store;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Endpoints;

/// <summary>Batch routes (spec §6): <c>POST /batches/{id}/undo</c> (FR-13). The id contains <c>#</c>, sent as <c>%23</c>.</summary>
public static class BatchEndpoints
{
    public sealed record UndoResponse(int Deleted, IReadOnlyList<UndoFailure> Failed);

    public static IEndpointRouteBuilder MapBatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/batches/{id}/undo", Undo);
        app.MapGet("/batches/{id}", Get);
        return app;
    }

    /// <summary>
    /// FR-A-11: the batch as last recorded — the poll target for a post that outlasted its synchronous budget. It
    /// reads <c>result.json</c>, falling back to <c>status.json</c> while the batch is still running, so the answer
    /// survives a restart of this process.
    /// </summary>
    private static IResult Get(string id, ApiBatchStore batches)
    {
        DirectPostResult? batch;
        try
        {
            batch = batches.Load(id);
        }
        catch (ArgumentException)
        {
            // The id is a path segment; one that is not a batch id is a bad request, not a server error.
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid batch id", detail: $"'{id}' is not a batch id");
        }

        return batch is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Batch not found", detail: $"batch {id} is not known")
            : Results.Ok(batch);
    }

    private static async Task<IResult> Undo(
        string id, JobQueue queue, BatchUndo undo, IJobStore store, IOptions<AppSettings> settings, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(BatchEndpoints));
        var label = id.Split('#')[0];
        log.LogInformation("Job {JobId}: undo of batch {BatchId} requested; queued", label, id);
        UndoResult result;
        try
        {
            // On the worker: undo writes the ledger and talks to QuickBooks, so it must not overlap a job.
            result = await queue.RunExclusiveAsync(label, token => UndoAndUpdateJobAsync(id, undo, store, settings.Value, token), ct);
        }
        catch (Exception ex) when (QuickBooksEndpoints.QuickBooksProblem(ex) is { } problem)
        {
            log.LogWarning("Job {JobId}: undo of batch {BatchId} failed, nothing marked undone: {Error}", label, id, ex.Message);
            return problem;
        }

        if (!result.Found)
        {
            log.LogWarning("Job {JobId}: batch {BatchId} is not in the ledger", label, id);
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Batch not found", detail: $"batch {id} is not in the ledger");
        }

        log.LogInformation(
            "Job {JobId}: undo of batch {BatchId}: {Deleted} deleted, {Failed} not deleted, batch undone {BatchUndone}",
            label, id, result.Deleted, result.Failed.Count, result.BatchUndone);
        foreach (var failure in result.Failed)
        {
            log.LogWarning("Job {JobId}: batch {BatchId}: TxnID {TxnId} not deleted: {Error}", label, id, failure.TxnId, failure.Message);
        }

        return Results.Ok(new UndoResponse(result.Deleted, result.Failed));
    }

    private static async Task<UndoResult> UndoAndUpdateJobAsync(
        string batchId, BatchUndo undo, IJobStore store, AppSettings settings, CancellationToken ct)
    {
        var job = store.All().FirstOrDefault(j => string.Equals(j.BatchId, batchId, StringComparison.OrdinalIgnoreCase));

        // SPEC-GAP T-606: audit copies go to the job's output folder; a batch whose job is no longer known uses qb-audit.
        var auditDir = job is null
            ? Path.Combine(Path.GetDirectoryName(settings.Paths.Ledger)!, QbListSync.AuditDir)
            : Path.Combine(job.Folder, FolderReader.OutputDirName);

        var result = await undo.UndoAsync(batchId, auditDir, ct);

        // Spec §6: posted|partial → undone, only when the whole batch is undone and it is still the job's current batch.
        if (result.BatchUndone && job is { Status: JobStatus.Posted or JobStatus.Partial })
        {
            store.Save(job.MoveTo(JobStatus.Undone));
        }

        return result;
    }
}
