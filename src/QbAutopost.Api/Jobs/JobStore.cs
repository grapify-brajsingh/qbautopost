using System.Text.Json;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Store;
using QbAutopost.Core.Text;

namespace QbAutopost.Api.Jobs;

/// <summary>
/// Job records in memory, mirrored to each job's <c>output/status.json</c> (CLAUDE.md rule 7) and listed in
/// <see cref="PathsSettings.JobIndex"/> so the job list survives a restart.
/// </summary>
public sealed class JobStore : IJobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JobRecord> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _indexPath;
    private readonly IClock _clock;
    private readonly ILogger<JobStore> _log;

    public JobStore(IOptions<AppSettings> settings, IClock clock, ILogger<JobStore> log)
    {
        _indexPath = settings.Value.Paths.JobIndex;
        _clock = clock;
        _log = log;
        Load();
    }

    public JobRecord? Get(string jobId)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(jobId);
        }
    }

    public IReadOnlyList<JobRecord> All()
    {
        lock (_gate)
        {
            return _jobs.Values
                .OrderByDescending(j => j.CreatedUtc)
                .ThenBy(j => j.JobId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public JobRecord Save(JobRecord record)
    {
        lock (_gate)
        {
            var saved = record with { UpdatedUtc = _clock.UtcNow };

            // status.json first: if the write fails, memory still shows the last persisted state.
            AtomicFile.WriteJson(saved.StatusFile, saved);

            var folderChanged = !_jobs.TryGetValue(saved.JobId, out var previous)
                                || !string.Equals(previous.Folder, saved.Folder, StringComparison.Ordinal);
            _jobs[saved.JobId] = saved;
            if (folderChanged)
            {
                SaveIndex();
            }

            return saved;
        }
    }

    private void SaveIndex()
    {
        var index = new JobIndex
        {
            Jobs = _jobs.Values
                .OrderBy(j => j.JobId, StringComparer.Ordinal)
                .Select(j => new JobIndexEntry(j.JobId, j.Folder))
                .ToList(),
        };
        AtomicFile.WriteJson(_indexPath, index);
    }

    private void Load()
    {
        if (!File.Exists(_indexPath))
        {
            return;
        }

        var index = JsonSerializer.Deserialize<JobIndex>(File.ReadAllText(_indexPath), JsonOptions.Default) ?? new JobIndex();
        foreach (var entry in index.Jobs)
        {
            var statusFile = JobRecord.StatusFileOf(entry.Folder);
            if (!File.Exists(statusFile))
            {
                _log.LogWarning("Job {JobId}: status file {StatusFile} not found; job left out of the list", entry.JobId, statusFile);
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(statusFile), JsonOptions.Default);
                if (record is not null)
                {
                    // The index decides where the job lives, even if the folder was moved after status.json was written.
                    _jobs[entry.JobId] = record with { JobId = entry.JobId, Folder = entry.Folder };
                }
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Job {JobId}: status file {StatusFile} is unreadable; job left out of the list", entry.JobId, statusFile);
            }
        }
    }

    private sealed record JobIndex
    {
        public IReadOnlyList<JobIndexEntry> Jobs { get; init; } = [];
    }

    private sealed record JobIndexEntry(string JobId, string Folder);
}
