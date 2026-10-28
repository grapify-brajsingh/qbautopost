using QbAutopost.Core.Api;

namespace QbAutopost.Core.SavedRequests;

public sealed record DuplicateDigest(
    long Id, string CanonicalSha256, string? Reference, IReadOnlySet<string> RowFingerprints, DateTime CreatedUtc);

public sealed record DuplicateMatch(long? Of, IReadOnlyList<string> Reasons)
{
    public bool Flagged => Reasons.Count > 0;
}

public static class DuplicateReasons
{
    public const string SamePayload = "same-payload";
    public const string SameReference = "same-reference";
    public const string OverlappingRows = "overlapping-rows";
}

/// <summary>Flags a saved request that looks like another one (spec §9). A flag informs; it never blocks.</summary>
public static class DuplicateDetector
{
    public static DuplicateMatch Detect(
        string canonicalSha256, string? reference, IReadOnlySet<string> rowFingerprints, IEnumerable<DuplicateDigest> others)
    {
        var reasons = new List<string>();
        DuplicateDigest? oldest = null;

        foreach (var other in others)
        {
            var found = new List<string>();
            if (other.CanonicalSha256 == canonicalSha256)
                found.Add(DuplicateReasons.SamePayload);
            if (!string.IsNullOrWhiteSpace(reference) && !string.IsNullOrWhiteSpace(other.Reference)
                && string.Equals(reference, other.Reference, StringComparison.OrdinalIgnoreCase))
                found.Add(DuplicateReasons.SameReference);
            if (rowFingerprints.Overlaps(other.RowFingerprints))
                found.Add(DuplicateReasons.OverlappingRows);
            if (found.Count == 0) continue;

            foreach (var r in found)
                if (!reasons.Contains(r)) reasons.Add(r);
            if (oldest is null || (other.CreatedUtc, other.Id).CompareTo((oldest.CreatedUtc, oldest.Id)) < 0)
                oldest = other;
        }

        return new DuplicateMatch(oldest?.Id, reasons);
    }

    /// <summary>The CLAUDE.md row fingerprints of every row that reads as a statement line.</summary>
    public static IReadOnlySet<string> RowFingerprints(SyncPostRequest request, DirectLimits limits, DateOnly today) =>
        new DirectRequestReader(limits).Read(request.ToDirectRequest(), today)
            .Lines.Select(l => l.Line.Fingerprint).ToHashSet(StringComparer.Ordinal);
}
