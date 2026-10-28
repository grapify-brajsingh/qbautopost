using QbAutopost.Core.Dashboard;
using Xunit;

namespace QbAutopost.Core.Tests.Dashboard;

public class TimeBucketsTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int mi = 0) =>
        new(y, m, d, h, mi, 0, DateTimeKind.Utc);

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    [Fact]
    public void Should_PutEventOnLocalDay_When_TzIsKolkata()
    {
        var at = Utc(2026, 10, 27, 20);
        var kolkata = TimeBuckets.Build(Utc(2026, 10, 26), Utc(2026, 10, 30), Zone("Asia/Kolkata"), BucketSize.Day);
        var utc = TimeBuckets.Build(Utc(2026, 10, 26), Utc(2026, 10, 30), TimeZoneInfo.Utc, BucketSize.Day);

        Assert.Equal(new DateOnly(2026, 10, 28), kolkata[TimeBuckets.IndexOf(kolkata, at)].Start);
        Assert.Equal(new DateOnly(2026, 10, 27), utc[TimeBuckets.IndexOf(utc, at)].Start);
    }

    [Fact]
    public void Should_MakeOneBucketPerLocalDay_When_RangeCrossesDst()
    {
        var ny = Zone("America/New_York");
        var from = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 31), ny);
        var to = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 3), ny);

        var buckets = TimeBuckets.Build(from, to, ny, BucketSize.Day);

        Assert.Equal(3, buckets.Count);
        var nov1 = buckets.Single(b => b.Start == new DateOnly(2026, 11, 1));
        Assert.Equal(TimeSpan.FromHours(25), nov1.ToUtc - nov1.FromUtc);
        Assert.All(buckets, b => Assert.Equal(DateTimeKind.Utc, b.FromUtc.Kind));
    }

    [Fact]
    public void Should_IncludeEmptyBuckets_When_NoEvents()
    {
        var buckets = TimeBuckets.Build(Utc(2026, 10, 1), Utc(2026, 10, 6), TimeZoneInfo.Utc, BucketSize.Day);

        Assert.Equal(5, buckets.Count);
        Assert.Equal(-1, TimeBuckets.IndexOf(buckets, Utc(2026, 10, 6)));
        Assert.Equal(-1, TimeBuckets.IndexOf(buckets, Utc(2026, 9, 30, 23)));
        Assert.Equal(4, TimeBuckets.IndexOf(buckets, Utc(2026, 10, 5, 23, 59)));
    }

    [Fact]
    public void Should_ClipFirstAndLastBucket_When_RangeStartsMidDay()
    {
        var from = Utc(2026, 10, 1, 9);
        var to = Utc(2026, 10, 3, 15);

        var buckets = TimeBuckets.Build(from, to, TimeZoneInfo.Utc, BucketSize.Day);

        Assert.Equal(3, buckets.Count);
        Assert.Equal(from, buckets[0].FromUtc);
        Assert.Equal(Utc(2026, 10, 2), buckets[0].ToUtc);
        Assert.Equal(Utc(2026, 10, 3), buckets[2].FromUtc);
        Assert.Equal(to, buckets[2].ToUtc);
        Assert.Equal(new DateOnly(2026, 10, 1), buckets[0].Start);
    }

    [Fact]
    public void Should_StartWeekOnMonday_When_BucketIsWeek()
    {
        var buckets = TimeBuckets.Build(Utc(2026, 10, 28), Utc(2026, 11, 10), TimeZoneInfo.Utc, BucketSize.Week);

        Assert.Equal(new DateOnly(2026, 10, 26), buckets[0].Start);
        Assert.Equal(new DateOnly(2026, 11, 2), buckets[1].Start);
        Assert.Equal(new DateOnly(2026, 11, 9), buckets[2].Start);
        Assert.Equal(Utc(2026, 10, 28), buckets[0].FromUtc);
        Assert.Equal(Utc(2026, 11, 2), buckets[0].ToUtc);
    }

    [Fact]
    public void Should_MakeDayTwentyThreeHours_When_SpringForward()
    {
        var ny = Zone("America/New_York");
        var from = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 3, 7), ny);
        var to = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 3, 10), ny);

        var buckets = TimeBuckets.Build(from, to, ny, BucketSize.Day);

        Assert.Equal(3, buckets.Count);
        var mar8 = buckets.Single(b => b.Start == new DateOnly(2026, 3, 8));
        Assert.Equal(TimeSpan.FromHours(23), mar8.ToUtc - mar8.FromUtc);
    }

    [Fact]
    public void Should_StartAtFirstValidTime_When_LocalMidnightDoesNotExist()
    {
        var beirut = Zone("Asia/Beirut");
        var day = new DateTime(2026, 3, 29);
        Assert.True(beirut.IsInvalidTime(day)); // tzdata: Lebanon springs forward at 00:00

        var buckets = TimeBuckets.Build(Utc(2026, 3, 28, 12), Utc(2026, 3, 29, 12), beirut, BucketSize.Day);

        var gapDay = buckets.Single(b => b.Start == new DateOnly(2026, 3, 29));
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 3, 29, 1, 0, 0), beirut), gapDay.FromUtc);
        Assert.Equal(Utc(2026, 3, 28, 22), gapDay.FromUtc);
        Assert.Equal(gapDay.FromUtc, buckets[0].ToUtc);
    }

    [Fact]
    public void Should_UseStandardTimeMidnight_When_MidnightIsAmbiguous()
    {
        var havana = Zone("America/Havana");
        Assert.True(havana.IsAmbiguousTime(new DateTime(2026, 11, 1))); // clocks go 01:00 -> 00:00

        var buckets = TimeBuckets.Build(Utc(2026, 10, 31, 12), Utc(2026, 11, 2, 12), havana, BucketSize.Day);

        var nov1 = buckets.Single(b => b.Start == new DateOnly(2026, 11, 1));
        // ConvertTimeToUtc takes the standard-time reading: 00:00 at UTC-5 = 05:00Z
        Assert.Equal(Utc(2026, 11, 1, 5), nov1.FromUtc);
        Assert.Equal(nov1.FromUtc, buckets[0].ToUtc);
    }

    [Fact]
    public void Should_ReturnNoBuckets_When_RangeIsEmptyOrReversed()
    {
        Assert.Empty(TimeBuckets.Build(Utc(2026, 10, 1), Utc(2026, 10, 1), TimeZoneInfo.Utc, BucketSize.Day));
        Assert.Empty(TimeBuckets.Build(Utc(2026, 10, 5), Utc(2026, 10, 1), TimeZoneInfo.Utc, BucketSize.Week));
    }

    [Fact]
    public void Should_KeepWeeksOnMonday_When_WeekCrossesDst()
    {
        var ny = Zone("America/New_York");
        var from = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 26), ny);
        var to = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 9), ny);

        var buckets = TimeBuckets.Build(from, to, ny, BucketSize.Week);

        Assert.Equal(2, buckets.Count);
        Assert.Equal(new DateOnly(2026, 10, 26), buckets[0].Start);
        Assert.Equal(new DateOnly(2026, 11, 2), buckets[1].Start);
        Assert.Equal(TimeSpan.FromHours(7 * 24 + 1), buckets[0].ToUtc - buckets[0].FromUtc);
        Assert.Equal(buckets[0].ToUtc, buckets[1].FromUtc);
    }

    [Fact]
    public void Should_NotAddEmptyBucket_When_FromIsOnBucketBoundary()
    {
        var buckets = TimeBuckets.Build(Utc(2026, 10, 2), Utc(2026, 10, 4), TimeZoneInfo.Utc, BucketSize.Day);

        Assert.Equal(2, buckets.Count);
        Assert.Equal(new DateOnly(2026, 10, 2), buckets[0].Start);
        Assert.Equal(Utc(2026, 10, 2), buckets[0].FromUtc);
        Assert.Equal(0, TimeBuckets.IndexOf(buckets, Utc(2026, 10, 2)));
        Assert.Equal(1, TimeBuckets.IndexOf(buckets, Utc(2026, 10, 3)));
    }
}
