using System.Globalization;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Hermes;

/// <summary>
/// Audit copies of Hermes calls (FR-17): <c>&lt;task&gt;-&lt;n&gt;.request.json</c> / <c>.response.json</c>, one pair per
/// HTTP attempt, numbered per task from 1. The API key travels in a header and is never part of these files.
/// </summary>
internal sealed class HermesAudit
{
    // Shorter keys (e.g. "dev") are not scrubbed: replacing them would garble ordinary words in the audit text.
    private const int MinScrubLength = 8;

    private readonly string? _dir;
    private readonly string _apiKey;
    private readonly string _prefix;

    public HermesAudit(string? dir, HermesTask task, string apiKey)
    {
        _dir = dir;
        _apiKey = apiKey;
        _prefix = task.ToString().ToLowerInvariant();
    }

    /// <summary>Reserves the next free number and writes the request; returns the number to use for the response.</summary>
    public int WriteRequest(string body)
    {
        if (_dir is null)
        {
            return 0;
        }

        Directory.CreateDirectory(_dir);
        var n = 1;
        while (File.Exists(PathOf(n, "request")))
        {
            n++;
        }

        AtomicFile.WriteAllText(PathOf(n, "request"), Scrub(body));
        return n;
    }

    public void WriteResponse(int n, string body)
    {
        if (_dir is not null)
        {
            AtomicFile.WriteAllText(PathOf(n, "response"), Scrub(body));
        }
    }

    private string PathOf(int n, string kind) =>
        Path.Combine(_dir!, string.Create(CultureInfo.InvariantCulture, $"{_prefix}-{n}.{kind}.json"));

    private string Scrub(string text) =>
        _apiKey.Length >= MinScrubLength ? text.Replace(_apiKey, "***", StringComparison.Ordinal) : text;
}
