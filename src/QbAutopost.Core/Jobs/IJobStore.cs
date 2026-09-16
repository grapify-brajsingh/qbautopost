namespace QbAutopost.Core.Jobs;

/// <summary>Job records in memory, mirrored to each job's <c>output/status.json</c> (plan §1).</summary>
public interface IJobStore
{
    JobRecord? Get(string jobId);

    /// <summary>Persists the record (status.json first, then memory) and returns it with <c>UpdatedUtc</c> set.</summary>
    JobRecord Save(JobRecord record);

    IReadOnlyList<JobRecord> All();
}
