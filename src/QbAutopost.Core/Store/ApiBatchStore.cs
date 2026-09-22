using System.Text.Json;
using QbAutopost.Core.Api;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Store;

/// <summary>
/// The evidence folder for one direct batch (api-v1 §6.3): <c>Paths:ApiBatches/&lt;batchId&gt;/</c> holding
/// <c>request.json</c>, <c>status.json</c>, <c>request.qbxml</c>, <c>response.qbxml</c> and <c>result.json</c> — the
/// same audit set FR-17 requires of a folder job.
/// <para>
/// This is deliberately not a second ledger. The ledger stays the one record of what was posted, and undo (FR-13)
/// finds a direct batch there exactly as it finds a folder one; this folder only answers "what was I asked to do,
/// and what happened".
/// </para>
/// </summary>
public sealed class ApiBatchStore(string root)
{
    public const string RequestFile = "request.json";
    public const string StatusFile = "status.json";
    public const string RequestQbXmlFile = "request.qbxml";
    public const string ResponseQbXmlFile = "response.qbxml";
    public const string ResultFile = "result.json";

    public string Root { get; } = root;

    /// <summary>The batch's folder. Throws when the id could name anything outside <see cref="Root"/>.</summary>
    public string FolderOf(string batchId)
    {
        // The id reaches here from a URL (FR-A-11), so it is treated as hostile until proven to be one folder name.
        // The test is an allow-list rather than Path.GetInvalidFileNameChars(), which is platform-dependent: on Linux
        // it permits '\' and ':', so a Windows path would sail through a check written against the running OS.
        if (string.IsNullOrWhiteSpace(batchId)
            || batchId.Contains("..", StringComparison.Ordinal)
            || !batchId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '#'))
        {
            throw new ArgumentException($"'{batchId}' is not a batch id.", nameof(batchId));
        }

        return Path.Combine(Root, batchId);
    }

    /// <summary>§6.3: the caller's request, verbatim, written before anything else happens to it.</summary>
    public void SaveRequest(string batchId, DirectRequest request) => Write(batchId, RequestFile, request);

    /// <summary>A transition while the batch runs (rule 7): on disk before the next step starts.</summary>
    public void SaveStatus(DirectPostResult status) => Write(status.BatchId, StatusFile, status);

    /// <summary>The final word. Written last, and preferred over <see cref="StatusFile"/> when both exist.</summary>
    public void SaveResult(DirectPostResult result) => Write(result.BatchId, ResultFile, result);

    public void SaveRequestQbXml(string batchId, string qbXml) => WriteText(batchId, RequestQbXmlFile, qbXml);

    public void SaveResponseQbXml(string batchId, string qbXml) => WriteText(batchId, ResponseQbXmlFile, qbXml);

    /// <summary>The batch as last recorded, or null when it is unknown. A finished batch answers from its result.</summary>
    public DirectPostResult? Load(string batchId)
    {
        var folder = FolderOf(batchId);
        return Read(Path.Combine(folder, ResultFile)) ?? Read(Path.Combine(folder, StatusFile));
    }

    /// <summary>Every batch on disk, newest first. Used to count attempts and, later, to age evidence out (Q-52).</summary>
    public IReadOnlyList<DirectPostResult> All()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(Root)
            .Select(folder => Read(Path.Combine(folder, ResultFile)) ?? Read(Path.Combine(folder, StatusFile)))
            .Where(batch => batch is not null)
            .Select(batch => batch!)
            .OrderByDescending(batch => batch.StartedUtc)
            .ToList();
    }

    /// <summary>
    /// Spec §3: batch id = job id + attempt. A re-post of the same reference is a new attempt, never the same id —
    /// the ledger and undo key off it, so reusing one would merge two postings into a single undoable batch.
    /// </summary>
    public string NextBatchId(string jobId)
    {
        var prefix = jobId + "#";
        var used = All()
            .Select(batch => batch.BatchId)
            .Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(id => int.TryParse(id[prefix.Length..], out var attempt) ? attempt : 0);

        return prefix + (used.DefaultIfEmpty(0).Max() + 1);
    }

    private void Write<T>(string batchId, string name, T value) =>
        AtomicFile.WriteJson(Path.Combine(Ensure(batchId), name), value);

    private void WriteText(string batchId, string name, string content) =>
        AtomicFile.WriteAllText(Path.Combine(Ensure(batchId), name), content);

    private string Ensure(string batchId)
    {
        var folder = FolderOf(batchId);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static DirectPostResult? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DirectPostResult>(AtomicFile.ReadAllText(path), JsonOptions.Default);
        }
        catch (JsonException)
        {
            // A half-written or hand-edited file must not take the API down; the batch simply reads as unknown.
            return null;
        }
    }
}
