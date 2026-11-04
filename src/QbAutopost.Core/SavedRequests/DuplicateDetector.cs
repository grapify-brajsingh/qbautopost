using QbAutopost.Core.Api;

namespace QbAutopost.Core.SavedRequests;

/// <param name="CompanyId">M17: the candidate's company; null when it has none yet.</param>
public sealed record DuplicateDigest(
    long Id, string CanonicalSha256, string? Reference, IReadOnlySet<string> RowFingerprints, DateTime CreatedUtc,
    int? CompanyId = null);

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

/// <summary>
/// Flags a saved request that looks like another one (spec §9). A flag informs; it never blocks.
/// <para>
/// M17 spec 4.6: <c>same-payload</c> compares with every other statement; <c>same-reference</c> and
/// <c>overlapping-rows</c> only with statements of the same company, so an unassigned statement (or candidate) is
/// compared with nothing for those two.
/// </para>
/// </summary>
public static class DuplicateDetector
{
    public static DuplicateMatch Detect(
        string canonicalSha256, string? reference, IReadOnlySet<string> rowFingerprints, IEnumerable<DuplicateDigest> others,
        int? companyId)
    {
        var reasons = new List<string>();
        DuplicateDigest? oldest = null;

        foreach (var other in others)
        {
            var found = new List<string>();
            if (other.CanonicalSha256 == canonicalSha256)
                found.Add(DuplicateReasons.SamePayload);
            var sameCompany = companyId is not null && other.CompanyId == companyId;
            if (sameCompany && !string.IsNullOrWhiteSpace(reference) && !string.IsNullOrWhiteSpace(other.Reference)
                && string.Equals(reference, other.Reference, StringComparison.OrdinalIgnoreCase))
                found.Add(DuplicateReasons.SameReference);
            if (sameCompany && rowFingerprints.Overlaps(other.RowFingerprints))
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
