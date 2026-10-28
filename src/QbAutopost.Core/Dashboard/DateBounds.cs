using System.Globalization;
using System.Text.RegularExpressions;

namespace QbAutopost.Core.Dashboard;

public static class DateBounds
{
    private static readonly Regex Instant = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Parses a bound. A plain yyyy-MM-dd is local midnight of that day (the next day when <paramref name="upper"/>),
    /// in <paramref name="tz"/> or UTC when null. An ISO instant must carry Z or an offset and is used as is.
    /// The result is UTC: inclusive for lower bounds, exclusive for upper ones.
    /// </summary>
    public static bool TryParse(string raw, TimeZoneInfo? tz, bool upper, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(raw)) return false;

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            if (upper) day = day.AddDays(1);
            utc = TimeBuckets.LocalMidnightUtc(day, tz ?? TimeZoneInfo.Utc);
            return true;
        }

        if (Instant.IsMatch(raw) &&
            DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            utc = DateTime.SpecifyKind(dto.UtcDateTime, DateTimeKind.Utc);
            return true;
        }
        return false;
    }

    /// <summary>Resolves an IANA zone id. Null or empty is UTC; an unknown id is false.</summary>
    public static bool TryZone(string? id, out TimeZoneInfo? tz)
    {
        if (string.IsNullOrWhiteSpace(id)) { tz = TimeZoneInfo.Utc; return true; }
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        { tz = null; return false; }
    }
}
