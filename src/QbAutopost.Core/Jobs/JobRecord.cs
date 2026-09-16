namespace QbAutopost.Core.Jobs;

/// <summary>
/// One job's lifecycle state, persisted as <c>output/status.json</c> (spec §5, §6).
/// Spec shape <c>{ status, updatedUtc, error?, attempt }</c>; the other fields let a restarted host rebuild the job list.
/// </summary>
public sealed record JobRecord
{
    public required string JobId { get; init; }
    public required string Folder { get; init; }
    public required JobStatus Status { get; init; }
    public bool DryRun { get; init; }

    /// <summary>Number of post attempts; the current batch id is <c>&lt;jobId&gt;#&lt;attempt&gt;</c>.</summary>
    public int Attempt { get; init; }

    public string? BatchId { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public string? Error { get; init; }

    public string StatusFile => StatusFileOf(Folder);

    public static string StatusFileOf(string folder) =>
        Path.Combine(folder, FolderReader.OutputDirName, "status.json");

    /// <summary>A copy in <paramref name="next"/> state; throws when spec §6 does not allow the move.</summary>
    public JobRecord MoveTo(JobStatus next, string? error = null)
    {
        if (!JobStatusRules.CanMove(Status, next))
        {
            throw new InvalidOperationException($"Job {JobId}: {Status} → {next} is not allowed.");
        }

        return this with { Status = next, Error = error };
    }
}
