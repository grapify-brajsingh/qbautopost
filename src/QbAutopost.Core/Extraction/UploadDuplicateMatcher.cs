using System.Text.Json;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Extraction;

public static class UploadDuplicateReasons
{
    public const string SameFile = "same-file";
}

public sealed record RowKey(string Kind, string Date, decimal Amount);

public sealed record UploadDuplicateCandidate(
    long Id, string? Reference, SavedRequestStatus Status, DateTime ReceivedUtc, DateTime CreatedUtc, string Source,
    string? UploadSha256, IReadOnlySet<string> RowFingerprints, IReadOnlyList<RowKey> RowKeys);

public sealed record UploadDuplicateHit(
    long Id, string? Reference, SavedRequestStatus Status, DateTime ReceivedUtc, string Source, IReadOnlyList<string> Reasons);

/// <summary>Possible duplicates of an upload (spec §6.7). Informs only; never blocks.</summary>
public static class UploadDuplicateMatcher
{
    public static IReadOnlyList<UploadDuplicateHit> Match(
        string sha256, string? reference, IReadOnlySet<string> fingerprints, IReadOnlyList<RowKey> rowKeys,
        IEnumerable<UploadDuplicateCandidate> candidates, int max = 5)
    {
        var uploaded = Count(rowKeys);
        var needed = Math.Max(1, (rowKeys.Count + 1) / 2);
        var hits = new List<(UploadDuplicateCandidate C, List<string> Reasons)>();

        foreach (var c in candidates)
        {
            var reasons = new List<string>();
            if (c.UploadSha256 is not null && string.Equals(c.UploadSha256, sha256, StringComparison.OrdinalIgnoreCase))
                reasons.Add(UploadDuplicateReasons.SameFile);
            if (!string.IsNullOrWhiteSpace(reference) && !string.IsNullOrWhiteSpace(c.Reference)
                && string.Equals(reference, c.Reference, StringComparison.OrdinalIgnoreCase))
                reasons.Add(DuplicateReasons.SameReference);
            if (fingerprints.Overlaps(c.RowFingerprints) || Overlaps(uploaded, rowKeys.Count, needed, c.RowKeys))
                reasons.Add(DuplicateReasons.OverlappingRows);
            if (reasons.Count > 0) hits.Add((c, reasons));
        }

        return hits.OrderByDescending(h => h.C.CreatedUtc).ThenByDescending(h => h.C.Id).Take(max)
            .Select(h => new UploadDuplicateHit(h.C.Id, h.C.Reference, h.C.Status, h.C.ReceivedUtc, h.C.Source, h.Reasons))
            .ToList();
    }

    // Kind is compared ignoring case (older payloads may differ in casing); date is exact; amount is decimal.
    // Multiset matching: each candidate row can pair with at most one uploaded row, so n identical uploaded
    // rows need n identical candidate rows. Matches never combine across candidates.
    private static bool Overlaps(Dictionary<(string, string, decimal), int> uploaded, int uploadedCount, int needed,
        IReadOnlyList<RowKey> candidateKeys)
    {
        if (uploadedCount == 0 || candidateKeys.Count == 0) return false;
        var theirs = Count(candidateKeys);
        var matched = 0;
        foreach (var (key, n) in uploaded)
            if (theirs.TryGetValue(key, out var m)) matched += Math.Min(n, m);
        return matched >= needed;
    }

    private static Dictionary<(string, string, decimal), int> Count(IEnumerable<RowKey> keys)
    {
        var d = new Dictionary<(string, string, decimal), int>();
        foreach (var k in keys)
        {
            var key = (k.Kind.ToUpperInvariant(), k.Date, k.Amount);
            d[key] = d.GetValueOrDefault(key) + 1;
        }
        return d;
    }

    public static IReadOnlyList<RowKey> RowKeysOf(JsonElement payload)
    {
        var keys = new List<RowKey>();
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("transactions", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return keys;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) continue;
            if (!row.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.String) continue;
            if (!row.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number
                || !amount.TryGetDecimal(out var value)) continue;
            keys.Add(new RowKey(kind.GetString()!, date.GetString()!, value));
        }
        return keys;
    }
}
