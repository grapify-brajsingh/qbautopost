using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Endpoints;

/// <summary>Batch routes (spec §6): <c>POST /batches/{id}/undo</c> (FR-13). The id contains <c>#</c>, sent as <c>%23</c>.</summary>
public static class BatchEndpoints
{
    public sealed record UndoResponse(int Deleted, IReadOnlyList<UndoFailure> Failed);

    public static IEndpointRouteBuilder MapBatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/batches/{id}/undo", Undo);
        return app;
    }

    private static async Task<IResult> Undo(
        string id, JobQueue queue, BatchUndo undo, IJobStore store, IOptions<AppSettings> settings, CancellationToken ct)
    {
        UndoResult result;
        try
        {
            // On the worker: undo writes the ledger and talks to QuickBooks, so it must not overlap a job.
            var label = id.Split('#')[0];
            result = await queue.RunExclusiveAsync(label, token => UndoAndUpdateJobAsync(id, undo, store, settings.Value, token), ct);
        }
        catch (Exception ex) when (QuickBooksEndpoints.QuickBooksProblem(ex) is { } problem)
        {
            return problem;
        }

        return result.Found
            ? Results.Ok(new UndoResponse(result.Deleted, result.Failed))
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Batch not found", detail: $"batch {id} is not in the ledger");
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
