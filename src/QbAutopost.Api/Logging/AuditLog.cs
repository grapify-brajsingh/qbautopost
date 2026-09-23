using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QbAutopost.Core.Text;

namespace QbAutopost.Api.Logging;

/// <summary>How a batch divided up, as the audit file records it. Null on a request that produced no batch.</summary>
public sealed record AuditCounts(
    [property: JsonPropertyName("submitted")] int Submitted,
    [property: JsonPropertyName("posted")] int Posted,
    [property: JsonPropertyName("held")] int Held,
    [property: JsonPropertyName("skipped")] int Skipped);

/// <summary>
/// FR-A-16: one line of the audit trail. Every field is named explicitly, because these names are the file format
/// an auditor reads years later — <c>JsonNamingPolicy.CamelCase</c> would only lower-case the first letter of
/// <c>TotalAmount</c> and leave <c>totalAmount</c> to luck (handoff trap 5).
/// <para>
/// What is deliberately absent: the API key, the request body, any statement description, any payee or account name.
/// The line says <i>who did what, when, and to how much money</i> — never what the money was for.
/// </para>
/// </summary>
public sealed record AuditEntry
{
    [JsonPropertyName("utc")]
    public required DateTime Utc { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("remoteIp")]
    public string? RemoteIp { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>The path only — never the query string, which a caller can put anything into.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("status")]
    public required int Status { get; init; }

    [JsonPropertyName("batchId")]
    public string? BatchId { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("counts")]
    public AuditCounts? Counts { get; init; }

    /// <summary>The money that actually moved (a batch's <c>totals.posted</c>); null when none did.</summary>
    [JsonPropertyName("totalAmount")]
    public decimal? TotalAmount { get; init; }
}

/// <summary>
/// FR-A-16: <c>Paths:Logs/audit-yyyyMMdd.jsonl</c>, one JSON object per line, kept for
/// <c>Api:AuditRetentionDays</c> (400 — money evidence outlives the 31-day operational log).
/// <para>
/// Deliberately <b>not</b> a Serilog sink (api-v1 §8): the operational log answers to
/// <c>Serilog:MinimumLevel</c>, and one configuration change to Warning would silently stop recording postings. This
/// file has no levels, no filters and no formatter to configure.
/// </para>
/// </summary>
public sealed class AuditLog(string folder, int retentionDays)
{
    public const string FilePrefix = "audit-";

    public const string FileExtension = ".jsonl";

    private const string DayFormat = "yyyyMMdd";

    /// <summary>One line per object, so a reader can tail the file; the shape is otherwise the app's JSON.</summary>
    private static readonly JsonSerializerOptions Line = new(JsonOptions.Default) { WriteIndented = false };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();

    private DateOnly? _sweptFor;

    public string Folder => folder;

    public static string FileNameFor(DateTime utc) =>
        FilePrefix + utc.ToUniversalTime().ToString(DayFormat, CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>Appends one line, and sweeps the folder the first time a given day is written to.</summary>
    public void Append(AuditEntry entry)
    {
        var day = DateOnly.FromDateTime(entry.Utc.ToUniversalTime());
        var json = JsonSerializer.Serialize(entry, Line);
        lock (_gate)
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(System.IO.Path.Combine(folder, FileNameFor(entry.Utc)), json + "\n", Utf8);
            if (_sweptFor != day)
            {
                _sweptFor = day;
                SweepCore(entry.Utc);
            }
        }
    }

    /// <summary>Deletes audit files older than the retention; returns how many went. Zero or less keeps them all.</summary>
    public int Sweep(DateTime utcNow)
    {
        lock (_gate)
        {
            return SweepCore(utcNow);
        }
    }

    private int SweepCore(DateTime utcNow)
    {
        if (retentionDays <= 0 || !Directory.Exists(folder))
        {
            // A misconfigured retention must never read as "delete the evidence".
            return 0;
        }

        var oldest = DateOnly.FromDateTime(utcNow.ToUniversalTime()).AddDays(-retentionDays);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(folder, FilePrefix + "*" + FileExtension).ToList())
        {
            // Only a file this class could have written is ever deleted; anything else in Paths:Logs is not ours.
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            if (name.Length == FilePrefix.Length + DayFormat.Length
                && DateOnly.TryParseExact(
                    name[FilePrefix.Length..], DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)
                && stamp < oldest)
            {
                File.Delete(file);
                deleted++;
            }
        }

        return deleted;
    }
}
