using QbAutopost.Core.Dashboard;
using Xunit;

namespace QbAutopost.Core.Tests.Dashboard;

public class DateBoundsTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int mi = 0) =>
        new(y, m, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Should_ResolveLocalDay_When_TzGiven()
    {
        Assert.True(DateBounds.TryZone("Asia/Kolkata", out var tz));

        Assert.True(DateBounds.TryParse("2026-10-27", tz, upper: false, out var lower));
        Assert.True(DateBounds.TryParse("2026-10-27", tz, upper: true, out var upper));

        Assert.Equal(Utc(2026, 10, 26, 18, 30), lower);
        Assert.Equal(Utc(2026, 10, 27, 18, 30), upper);
        Assert.Equal(DateTimeKind.Utc, lower.Kind);
        Assert.Equal(DateTimeKind.Utc, upper.Kind);
    }

    [Fact]
    public void Should_KeepUtcDay_When_NoTz()
    {
        Assert.True(DateBounds.TryParse("2026-10-27", null, upper: false, out var lower));
        Assert.True(DateBounds.TryParse("2026-10-27", null, upper: true, out var upper));

        Assert.Equal(Utc(2026, 10, 27), lower);
        Assert.Equal(Utc(2026, 10, 28), upper);
        Assert.Equal(DateTimeKind.Utc, upper.Kind);
    }

    [Fact]
    public void Should_UseInstantAsIs_When_OffsetGiven()
    {
        Assert.True(DateBounds.TryZone("Asia/Kolkata", out var tz));

        Assert.True(DateBounds.TryParse("2026-10-27T10:00:00+05:30", tz, upper: true, out var a));
        Assert.True(DateBounds.TryParse("2026-10-27T10:00:00Z", tz, upper: false, out var b));

        Assert.Equal(Utc(2026, 10, 27, 4, 30), a);
        Assert.Equal(Utc(2026, 10, 27, 10), b);
        Assert.Equal(DateTimeKind.Utc, a.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("2026-10-27T10:00:00")]
    [InlineData("2026-1-27")]
    [InlineData("10/27/2026")]
    public void Should_RejectInput_When_NotDateOrInstantWithOffset(string raw)
    {
        Assert.False(DateBounds.TryParse(raw, null, upper: false, out _));
    }

    [Fact]
    public void Should_RejectUnknownZone_When_TzInvalid()
    {
        Assert.False(DateBounds.TryZone("Mars/Olympus", out _));
        Assert.True(DateBounds.TryZone(null, out var utc));
        Assert.Equal(TimeZoneInfo.Utc, utc);
        Assert.True(DateBounds.TryZone("", out _));
    }
}
