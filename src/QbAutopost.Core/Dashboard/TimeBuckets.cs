namespace QbAutopost.Core.Dashboard;

public enum BucketSize { Day, Week }

/// <summary>One chart bucket: the local date it starts on, and its UTC span clipped to the requested range.</summary>
public sealed record TimeBucket(DateOnly Start, DateTime FromUtc, DateTime ToUtc);

public static class TimeBuckets
{
    /// <summary>Every bucket overlapping [fromUtc, toUtc), clipped to it, in order. Weeks start on Monday.</summary>
    public static IReadOnlyList<TimeBucket> Build(DateTime fromUtc, DateTime toUtc, TimeZoneInfo tz, BucketSize size)
    {
        var result = new List<TimeBucket>();
        if (toUtc <= fromUtc) return result;

        var localFrom = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(fromUtc), tz);
        var start = DateOnly.FromDateTime(localFrom);
        if (size == BucketSize.Week)
            start = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
        var step = size == BucketSize.Week ? 7 : 1;

        while (true)
        {
            var next = start.AddDays(step);
            var bucketFrom = LocalMidnightUtc(start, tz);
            var bucketTo = LocalMidnightUtc(next, tz);
            if (bucketFrom >= toUtc) break;

            var clippedFrom = bucketFrom < fromUtc ? AsUtc(fromUtc) : bucketFrom;
            var clippedTo = bucketTo > toUtc ? AsUtc(toUtc) : bucketTo;
            if (clippedTo > clippedFrom)
                result.Add(new TimeBucket(start, clippedFrom, clippedTo));
            start = next;
        }
        return result;
    }

    /// <summary>Index of the bucket containing <paramref name="atUtc"/>, or -1 when outside all buckets.</summary>
    public static int IndexOf(IReadOnlyList<TimeBucket> buckets, DateTime atUtc)
    {
        for (var i = 0; i < buckets.Count; i++)
            if (atUtc >= buckets[i].FromUtc && atUtc < buckets[i].ToUtc) return i;
        return -1;
    }

    /// <summary>Local midnight of <paramref name="day"/> in UTC; inside a DST gap, the first valid local time after it.</summary>
    internal static DateTime LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(30);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, tz), DateTimeKind.Utc);
    }

    private static DateTime AsUtc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
