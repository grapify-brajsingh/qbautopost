using QbAutopost.Core.Dashboard;

namespace QbAutopost.Core.Tests.Dashboard;

/// <summary>T-1212a fix round 1 (M-4): an instant ends at the end of the text; a trailing newline is not ignored.</summary>
public sealed class DateBoundsStrictInstantTests
{
    [Theory]
    [InlineData("2026-10-27T00:00:00Z\n")]
    [InlineData("2026-10-27T00:00:00+05:30\n")]
    public void Should_Reject_When_InstantHasTrailingNewline(string raw) =>
        Assert.False(DateBounds.TryParse(raw, null, upper: false, out _));

    [Fact]
    public void Should_Accept_When_InstantEndsAtOffset() =>
        Assert.True(DateBounds.TryParse("2026-10-27T00:00:00+05:30", null, upper: false, out _));
}
