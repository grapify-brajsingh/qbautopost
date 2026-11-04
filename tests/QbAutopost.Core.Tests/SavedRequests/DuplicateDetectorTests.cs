using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Tests.SavedRequests;

public class DuplicateDetectorTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlySet<string> Fp(params string[] f) => f.ToHashSet();

    private const int Takoma = 2;
    private const int Sa = 3;

    private static DuplicateDigest Other(
        long id, string sha = "x", string? reference = null, string[]? fps = null, int days = 0, int? company = Takoma) =>
        new(id, sha, reference, Fp(fps ?? []), T0.AddDays(days), company);

    [Fact]
    public void Should_FlagSamePayload_When_HashesEqual()
    {
        var m = DuplicateDetector.Detect("abc", null, Fp(), [Other(5, "abc")], Takoma);

        Assert.Equal(5, m.Of);
        Assert.Equal([DuplicateReasons.SamePayload], m.Reasons);
    }

    [Fact]
    public void Should_FlagSameReference_When_ReferenceEqualIgnoringCase()
    {
        var m = DuplicateDetector.Detect("a", "Zemen-1", Fp(), [Other(2, reference: "ZEMEN-1")], Takoma);

        Assert.Equal([DuplicateReasons.SameReference], m.Reasons);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Should_NotFlagReference_When_Empty(string? reference)
    {
        var m = DuplicateDetector.Detect("a", reference, Fp(), [Other(2, reference: reference)], Takoma);

        Assert.False(m.Flagged);
    }

    [Fact]
    public void Should_FlagOverlappingRows_When_AnyFingerprintShared()
    {
        var m = DuplicateDetector.Detect("a", null, Fp("f1", "f2"), [Other(3, fps: ["f2", "f9"])], Takoma);

        Assert.Equal([DuplicateReasons.OverlappingRows], m.Reasons);
        Assert.Equal(3, m.Of);
    }

    [Fact]
    public void Should_ReportOldestMatchAndAllReasons_When_SeveralMatch()
    {
        var m = DuplicateDetector.Detect("a", "r", Fp("f1"),
        [
            Other(9, "a", days: 2),
            Other(7, reference: "R", days: 1),
            Other(8, fps: ["f1"], days: 1),
            Other(1, "zzz", reference: "none", days: -5),
        ], Takoma);

        Assert.Equal(7, m.Of);
        Assert.Equivalent(
            new[] { DuplicateReasons.SamePayload, DuplicateReasons.SameReference, DuplicateReasons.OverlappingRows },
            m.Reasons);
    }

    [Fact]
    public void Should_ReturnNotFlagged_When_NoOthers()
    {
        var m = DuplicateDetector.Detect("a", "r", Fp("f"), [], Takoma);

        Assert.False(m.Flagged);
        Assert.Null(m.Of);
    }

    // ---- M17 spec 4.6: same-reference and overlapping-rows only within one company ----------------------------

    [Fact]
    public void Should_FlagSamePayload_When_CompaniesDiffer()
    {
        var m = DuplicateDetector.Detect("abc", null, Fp(), [Other(5, "abc", company: Sa)], Takoma);

        Assert.Equal([DuplicateReasons.SamePayload], m.Reasons);
    }

    [Fact]
    public void Should_NotFlagSameReference_When_CompaniesDiffer()
    {
        var m = DuplicateDetector.Detect("a", "Zemen-1", Fp(), [Other(2, reference: "Zemen-1", company: Sa)], Takoma);

        Assert.False(m.Flagged);
    }

    [Fact]
    public void Should_NotFlagOverlappingRows_When_EitherStatementHasNoCompany()
    {
        var unassignedOther = DuplicateDetector.Detect("a", "r", Fp("f1"), [Other(3, reference: "r", fps: ["f1"], company: null)], Takoma);
        var unassignedSelf = DuplicateDetector.Detect("a", "r", Fp("f1"), [Other(3, reference: "r", fps: ["f1"])], null);

        Assert.False(unassignedOther.Flagged);
        Assert.False(unassignedSelf.Flagged);
    }

    [Fact]
    public void Should_FlagSameReferenceAndRows_When_SameCompany()
    {
        var m = DuplicateDetector.Detect("a", "r", Fp("f1"), [Other(3, reference: "R", fps: ["f1"], company: Takoma)], Takoma);

        Assert.Equal([DuplicateReasons.SameReference, DuplicateReasons.OverlappingRows], m.Reasons);
    }
}
