using System.Text.Json;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;
using Xunit;

namespace QbAutopost.Core.Tests.Extraction;

public class UploadDuplicateMatcherTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RowKey K(string kind, string date, decimal amount) => new(kind, date, amount);

    private static UploadDuplicateCandidate Cand(
        long id, string? reference = null, string? sha = null, DateTime? created = null,
        string[]? fps = null, RowKey[]? keys = null) =>
        new(id, reference, SavedRequestStatus.Pending, T0, created ?? T0.AddDays(id), "upload", sha,
            (fps ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal), keys ?? Array.Empty<RowKey>());

    private static IReadOnlyList<UploadDuplicateHit> Match(
        IEnumerable<UploadDuplicateCandidate> cands, string sha = "sha", string? reference = null,
        string[]? fps = null, RowKey[]? keys = null, int max = 5) =>
        UploadDuplicateMatcher.Match(sha, reference, (fps ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal),
            keys ?? Array.Empty<RowKey>(), cands, max);

    [Fact]
    public void Should_FlagSameFile_When_ShaMatchesSavedUpload()
    {
        var hits = Match(new[] { Cand(1, sha: "sha") });

        Assert.Equal(new[] { UploadDuplicateReasons.SameFile }, Assert.Single(hits).Reasons);
    }

    [Fact]
    public void Should_FlagSameReference_When_CaseDiffers()
    {
        var hits = Match(new[] { Cand(1, reference: "inv-001") }, reference: "INV-001");

        Assert.Equal(new[] { DuplicateReasons.SameReference }, Assert.Single(hits).Reasons);
    }

    [Fact]
    public void Should_NotFlagReference_When_Blank()
    {
        var hits = Match(new[] { Cand(1, reference: " "), Cand(2, reference: null) }, reference: " ");

        Assert.Empty(hits);
    }

    [Fact]
    public void Should_FlagOverlap_When_OneFingerprintMatches()
    {
        var hits = Match(new[] { Cand(1, fps: new[] { "a", "b" }) }, fps: new[] { "b", "c", "d", "e" });

        Assert.Equal(new[] { DuplicateReasons.OverlappingRows }, Assert.Single(hits).Reasons);
    }

    [Fact]
    public void Should_FlagOverlap_When_HalfOfRowsMatchByKindDateAmount()
    {
        var rows = new[] { K("Deposit", "2026-01-01", 1m), K("Deposit", "2026-01-02", 2m), K("Check", "2026-01-03", 3m), K("Check", "2026-01-04", 4m) };
        var cand = Cand(1, keys: new[] { K("deposit", "2026-01-01", 1.00m), K("Deposit", "2026-01-02", 2m), K("Check", "2026-02-03", 3m) });

        var hits = Match(new[] { cand }, keys: rows);

        Assert.Equal(new[] { DuplicateReasons.OverlappingRows }, Assert.Single(hits).Reasons);
    }

    [Fact]
    public void Should_NotFlagOverlap_When_MatchesSpreadOverCandidates()
    {
        var rows = new[] { K("Deposit", "2026-01-01", 1m), K("Deposit", "2026-01-02", 2m), K("Check", "2026-01-03", 3m), K("Check", "2026-01-04", 4m) };
        var hits = Match(new[]
        {
            Cand(1, keys: new[] { K("Deposit", "2026-01-01", 1m) }),
            Cand(2, keys: new[] { K("Deposit", "2026-01-02", 2m) }),
        }, keys: rows);

        Assert.Empty(hits);
    }

    [Fact]
    public void Should_CountIdenticalUploadedRowsOnce_When_CandidateHasOnlyOne()
    {
        var rows = new[] { K("Check", "2026-01-01", 5m), K("Check", "2026-01-01", 5m), K("Check", "2026-02-01", 9m), K("Check", "2026-02-02", 9m) };
        var hits = Match(new[] { Cand(1, keys: new[] { K("Check", "2026-01-01", 5m) }) }, keys: rows);

        Assert.Empty(hits);
    }

    [Fact]
    public void Should_ReturnAtMostFiveNewestFirst_When_ManyMatch()
    {
        var cands = Enumerable.Range(1, 8).Select(i => Cand(i, sha: "sha"));

        var hits = Match(cands);

        Assert.Equal(new long[] { 8, 7, 6, 5, 4 }, hits.Select(h => h.Id));
    }

    [Fact]
    public void Should_ListAllReasons_When_SeveralApply()
    {
        var cand = Cand(1, reference: "R1", sha: "sha", fps: new[] { "x" });

        var hit = Assert.Single(Match(new[] { cand }, reference: "r1", fps: new[] { "x" }));

        Assert.Equal(new[] { "same-file", "same-reference", "overlapping-rows" }, hit.Reasons);
    }

    [Fact]
    public void Should_SkipRows_When_KindDateOrAmountMissing()
    {
        using var doc = JsonDocument.Parse("""
            { "transactions": [
              { "kind": "Deposit", "date": "2026-01-01", "amount": 12.50 },
              { "date": "2026-01-01", "amount": 1 },
              { "kind": "Check", "amount": 1 },
              { "kind": "Check", "date": "2026-01-01", "amount": "1" },
              { "kind": "Check", "date": "2026-01-01" } ] }
            """);

        var keys = UploadDuplicateMatcher.RowKeysOf(doc.RootElement);

        Assert.Equal(new[] { K("Deposit", "2026-01-01", 12.50m) }, keys);
    }
}
