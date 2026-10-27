using System.Security.Cryptography;
using System.Text.Json;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Store;

/// <summary>What should happen to a request carrying an <c>Idempotency-Key</c> (FR-A-12).</summary>
public enum IdempotencyVerdict
{
    /// <summary>First use: the key is now held by this request, which should run normally.</summary>
    Claimed,

    /// <summary>The same request is still running. The caller waits; nothing is started twice.</summary>
    InProgress,

    /// <summary>The same request already finished: answer with what it answered, and send nothing to QuickBooks.</summary>
    Replay,

    /// <summary>One key, two different bodies. Neither answer would be true of the other request.</summary>
    Conflict,
}

/// <summary>One key and, once it has finished, the answer it produced.</summary>
public sealed record IdempotencyEntry
{
    public required string Key { get; init; }

    /// <summary>Method and path, so a caller numbering their requests cannot make an undo collide with a post.</summary>
    public required string Route { get; init; }

    /// <summary>SHA-256 of the request body, lower-hex: what makes a replay a replay and not a new request.</summary>
    public required string BodyHash { get; init; }

    /// <summary>SPEC-GAP T-909: FR-A-12 scopes keys per client, but clients do not exist until T-910; null until then.</summary>
    public string? ClientId { get; init; }

    public DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public int StatusCode { get; init; }

    /// <summary>The response body to replay. Never a secret: these routes answer with batches, not credentials.</summary>
    public string? Response { get; init; }

    public string? ContentType { get; init; }

    public string? BatchId { get; init; }

    public bool IsComplete => CompletedUtc is not null;
}

/// <summary>The verdict and, for a replay, the entry holding the answer.</summary>
public sealed record IdempotencyClaim(IdempotencyVerdict Verdict, IdempotencyEntry? Entry);

/// <summary>Everything the store keeps, as it sits in <c>idempotency.json</c>.</summary>
public sealed record IdempotencyLog
{
    public IReadOnlyList<IdempotencyEntry> Entries { get; init; } = [];
}

/// <summary>
/// FR-A-12: <c>Paths:ApiBatches/idempotency.json</c>. A caller who retries after a dropped connection gets the first
/// answer back instead of a second posting.
/// <para>
/// This is the convenience layer. The safety layer is G4 and the ledger, which catch a repeat whether or not a key
/// was sent — so when a request is refused before it does anything, its key is <b>released</b> rather than held: a
/// caller who fixes a typo and resends with the same key should be served, not told off.
/// </para>
/// <para>
/// Locking is in-process, which is what this app is (ADR-0003: one host, one worker). Two hosts over one file would
/// need a real lock, and would break far more than this.
/// </para>
/// </summary>
public sealed class IdempotencyStore(string filePath, int retentionDays = 30)
{
    private readonly object _gate = new();

    public string FilePath { get; } = filePath;

    /// <summary>The body hash FR-A-12 compares, lower-hex SHA-256.</summary>
    public static string HashOf(ReadOnlySpan<byte> body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    /// <summary>
    /// Takes the key for this request, or explains why it cannot. Expired entries are dropped as we pass, so the file
    /// stays the size of the retention window rather than of all time.
    /// </summary>
    public IdempotencyClaim Claim(string key, string route, string bodyHash, string? clientId, DateTime utcNow)
    {
        lock (_gate)
        {
            var kept = Load().Entries.Where(e => e.StartedUtc > utcNow.AddDays(-retentionDays)).ToList();
            var existing = kept.FirstOrDefault(e => Same(e, key, route, clientId));
            if (existing is null)
            {
                Save(kept.Append(new IdempotencyEntry
                {
                    Key = key,
                    Route = route,
                    BodyHash = bodyHash,
                    ClientId = clientId,
                    StartedUtc = utcNow,
                }));
                return new IdempotencyClaim(IdempotencyVerdict.Claimed, null);
            }

            // Written back so the pruning above is not lost when an existing key short-circuits the call.
            Save(kept);
            if (!string.Equals(existing.BodyHash, bodyHash, StringComparison.Ordinal))
            {
                return new IdempotencyClaim(IdempotencyVerdict.Conflict, existing);
            }

            return existing.IsComplete
                ? new IdempotencyClaim(IdempotencyVerdict.Replay, existing)
                : new IdempotencyClaim(IdempotencyVerdict.InProgress, existing);
        }
    }

    /// <summary>Records the answer, so the next identical request is served from here instead of QuickBooks.</summary>
    public void Complete(
        string key, string route, int statusCode, string? response, string? contentType, string? batchId, DateTime utcNow,
        string? clientId = null)
    {
        lock (_gate)
        {
            var entries = Load().Entries.ToList();
            var index = entries.FindIndex(e => Same(e, key, route, clientId));
            var entry = index >= 0
                ? entries[index]
                : new IdempotencyEntry { Key = key, Route = route, BodyHash = "", ClientId = clientId, StartedUtc = utcNow };

            entry = entry with
            {
                CompletedUtc = utcNow,
                StatusCode = statusCode,
                Response = response,
                ContentType = contentType,
                BatchId = batchId,
            };

            if (index >= 0)
            {
                entries[index] = entry;
            }
            else
            {
                entries.Add(entry);
            }

            Save(entries);
        }
    }

    /// <summary>
    /// Gives the key back. Used when the request was refused before it did anything, so a caller who corrects it and
    /// resends with the same key is not locked out of their own key by a typo.
    /// </summary>
    public void Release(string key, string route, string? clientId = null)
    {
        lock (_gate)
        {
            Save(Load().Entries.Where(e => !Same(e, key, route, clientId)));
        }
    }

    private static bool Same(IdempotencyEntry entry, string key, string route, string? clientId) =>
        string.Equals(entry.Key, key, StringComparison.Ordinal)
        && string.Equals(entry.Route, route, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.ClientId, clientId, StringComparison.Ordinal);

    private IdempotencyLog Load()
    {
        if (!File.Exists(FilePath))
        {
            return new IdempotencyLog();
        }

        try
        {
            return JsonSerializer.Deserialize<IdempotencyLog>(AtomicFile.ReadAllText(FilePath), JsonOptions.Default)
                   ?? new IdempotencyLog();
        }
        catch (JsonException)
        {
            // Bookkeeping, not money: a corrupt file costs one un-deduplicated retry, which G4 still catches. Taking
            // the API down over it would be the worse failure.
            return new IdempotencyLog();
        }
    }

    private void Save(IEnumerable<IdempotencyEntry> entries) =>
        AtomicFile.WriteJson(FilePath, new IdempotencyLog { Entries = entries.ToList() });
}
