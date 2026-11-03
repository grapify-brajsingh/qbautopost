using System.Globalization;
using QbAutopost.Core.Dashboard;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.SavedRequests;

/// <summary>
/// Dashboard v2 spec 9.2: the query of <c>GET /api/v2/saved-requests</c>. v1's parameters, read as the v1 list reads
/// them, plus <c>receivedFrom</c>, <c>receivedTo</c>, <c>emailFrom</c>, <c>reference</c>, <c>minTotal</c>,
/// <c>maxTotal</c>, <c>hasErrors</c> and the v2 sorts. Every value is checked; one that is not understood is an error,
/// never ignored.
/// </summary>
public static class V2QueryParser
{
    public const int DefaultPageSize = 25;
    public const string DefaultSort = "-created";

    public static IReadOnlyList<string> Sorts { get; } =
    [
        "created", "-created", "updated", "-updated", "controlTotal", "-controlTotal",
        "received", "-received", "total", "-total", "rows", "-rows", "posted", "-posted",
    ];

    private static readonly Dictionary<string, SavedRequestStatus> StatusNames =
        Enum.GetValues<SavedRequestStatus>().ToDictionary(s => SavedRequestJson.Name(s), StringComparer.Ordinal);

    /// <param name="maxPageSize">The largest <c>pageSize</c> (<c>SavedRequests:MaxListPageSize</c>), at least 1.</param>
    /// <param name="error">When false: the <c>detail</c> of the <c>400 invalid-request</c> answer.</param>
    public static bool TryParse(IQueryCollection q, int maxPageSize, out SavedRequestQueryV2? query, out string? error)
    {
        query = null;
        error = null;

        var statuses = new List<SavedRequestStatus>();
        foreach (var text in q["status"])
        {
            if (text is null || !StatusNames.TryGetValue(text, out var status))
            {
                error = $"status must be one of {string.Join(", ", StatusNames.Keys)}.";
                return false;
            }

            statuses.Add(status);
        }

        if (!TryBool(q, "needsAttention", out var needsAttention) || !TryBool(q, "duplicate", out var duplicate)
            || !TryBool(q, "hasErrors", out var hasErrors))
        {
            error = "needsAttention, duplicate and hasErrors must be true or false.";
            return false;
        }

        // T-1212a (dashboard-api-m12 5): tz makes a plain date a local day; without it a plain date stays a UTC day.
        TimeZoneInfo? tz = null;
        if (q.TryGetValue("tz", out var tzText) && (!IsZoneId(tzText.ToString()) || !DateBounds.TryZone(tzText.ToString(), out tz)))
        {
            error = "tz is an IANA time zone id, such as Asia/Kolkata or UTC.";
            return false;
        }

        if (!TryBound(q, "createdFrom", tz, false, out var createdFrom, out var createdFromUtc)
            || !TryBound(q, "createdTo", tz, true, out var createdTo, out var createdToUtc)
            || !TryBound(q, "receivedFrom", tz, false, out var receivedFrom, out var receivedFromUtc)
            || !TryBound(q, "receivedTo", tz, true, out var receivedTo, out var receivedToUtc)
            || !TryBound(q, "postedFrom", tz ?? TimeZoneInfo.Utc, false, out _, out var postedFromUtc)
            || !TryBound(q, "postedTo", tz ?? TimeZoneInfo.Utc, true, out _, out var postedToUtc))
        {
            error = "createdFrom, createdTo, receivedFrom, receivedTo, postedFrom and postedTo are dates, yyyy-MM-dd "
                + "(a UTC day, or a local day in tz), or ISO 8601 instants with an offset, such as 2026-10-27T00:00:00Z "
                + "or 2026-10-27T00:00:00+05:30 (a + in a URL is written %2B).";
            return false;
        }

        if (!TryDecimal(q, "minTotal", out var minTotal) || !TryDecimal(q, "maxTotal", out var maxTotal))
        {
            error = "minTotal and maxTotal are numbers, such as 1250.00 (a '.' before the decimals, no thousands separator).";
            return false;
        }

        if (minTotal > maxTotal)
        {
            error = "minTotal must not be greater than maxTotal.";
            return false;
        }

        var sort = q.TryGetValue("sort", out var sortText) ? sortText.ToString() : DefaultSort;
        if (!Sorts.Contains(sort, StringComparer.Ordinal))
        {
            error = $"sort must be one of {string.Join(", ", Sorts)}.";
            return false;
        }

        var max = Math.Max(1, maxPageSize);
        if (Paging(q, max) is not { } paging)
        {
            error = $"page must be 1 or more and pageSize between 1 and {max}.";
            return false;
        }

        query = new SavedRequestQueryV2(
            new SavedRequestQuery(
                statuses, needsAttention, duplicate, Text(q, "q"), createdFrom, createdTo, Text(q, "source"), sort,
                paging.Page, paging.PageSize),
            receivedFrom, receivedTo, Text(q, "emailFrom"), Text(q, "reference"), minTotal, maxTotal, hasErrors)
        {
            ReceivedFromUtc = receivedFromUtc,
            ReceivedToUtc = receivedToUtc,
            CreatedFromUtc = createdFromUtc,
            CreatedToUtc = createdToUtc,
            PostedFromUtc = postedFromUtc,
            PostedToUtc = postedToUtc,
        };
        return true;
    }

    /// <summary>
    /// A sent <c>tz</c> names a zone: not blank and not padded. Nothing is trimmed, and blank is never read as UTC
    /// (<see cref="DateBounds.TryZone"/> alone would).
    /// </summary>
    public static bool IsZoneId(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length == text.Length;

    /// <summary>The trimmed value, or null when it is absent or blank.</summary>
    private static string? Text(IQueryCollection q, string name) =>
        q.TryGetValue(name, out var text) && !string.IsNullOrWhiteSpace(text.ToString()) ? text.ToString().Trim() : null;

    private static bool TryBool(IQueryCollection q, string name, out bool? value)
    {
        value = null;
        if (!q.TryGetValue(name, out var text))
        {
            return true;
        }

        if (!bool.TryParse(text.ToString(), out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// A date filter (spec dashboard-api-m12 5.3, 5.4). Absent is fine. With <paramref name="tz"/> null, a plain date
    /// keeps its v2 meaning and comes back in <paramref name="date"/> (an inclusive UTC day, compared as today). Every
    /// other form comes back in <paramref name="utc"/> through <see cref="DateBounds.TryParse"/>: a plain date as the
    /// local day's start (or the next day's start when <paramref name="upper"/>), an instant with an offset as is. From
    /// is inclusive, To exclusive.
    /// </summary>
    private static bool TryBound(
        IQueryCollection q, string name, TimeZoneInfo? tz, bool upper, out DateOnly? date, out DateTime? utc)
    {
        date = null;
        utc = null;
        if (!q.TryGetValue(name, out var values))
        {
            return true;
        }

        var text = values.ToString();
        if (tz is null
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            date = day;
            return true;
        }

        if (!DateBounds.TryParse(text, tz, upper, out var bound))
        {
            return false;
        }

        utc = bound;
        return true;
    }

    /// <summary>An invariant decimal: an optional sign, digits and an optional <c>.</c>; nothing else.</summary>
    private static bool TryDecimal(IQueryCollection q, string name, out decimal? value)
    {
        value = null;
        if (!q.TryGetValue(name, out var text))
        {
            return true;
        }

        if (!decimal.TryParse(
                text.ToString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>(page, pageSize), or null when either is not a whole number in range.</summary>
    internal static (int Page, int PageSize)? Paging(IQueryCollection q, int maxPageSize)
    {
        var page = 1;
        var pageSize = Math.Min(DefaultPageSize, maxPageSize);
        if (q.TryGetValue("page", out var pageText)
            && (!int.TryParse(pageText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
        {
            return null;
        }

        if (q.TryGetValue("pageSize", out var sizeText)
            && (!int.TryParse(sizeText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize < 1 || pageSize > maxPageSize))
        {
            return null;
        }

        return (page, pageSize);
    }
}
