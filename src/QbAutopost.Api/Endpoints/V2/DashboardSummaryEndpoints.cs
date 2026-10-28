using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QbAutopost.Api.Database;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Core.Dashboard;

namespace QbAutopost.Api.Endpoints.V2;

/// <summary>
/// Dashboard-api-m12 4 (R-11): <c>GET /api/v2/dashboard/summary</c>, the Home figures. The scope is
/// <c>requests:read</c> (<see cref="ApiRoutes.ScopeFor"/>). The counts, money and series come from
/// <see cref="DashboardSummaryBuilder"/> over the store's facts; this endpoint only checks the query, cuts the buckets and
/// echoes the range.
/// </summary>
public static class DashboardSummaryEndpoints
{
    public const int MaxRangeDays = 366;

    private static readonly string[] SingleValued = ["from", "to", "compareFrom", "compareTo", "bucket", "tz"];

    private static readonly Regex Instant = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapDashboardSummaryEndpoints(this IEndpointRouteBuilder v2)
    {
        v2.MapGroup("/dashboard/summary").RequireDatabase().MapGet(string.Empty, SummaryAsync);
        return v2;
    }

    private static async Task<IResult> SummaryAsync(HttpContext http, IDashboardSummaryStore store, CancellationToken ct)
    {
        if (!TryParse(http.Request.Query, out var query, out var error))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid query", detail: error, type: "invalid-request");
        }

        var q = query!;
        var buckets = TimeBuckets.Build(q.FromUtc, q.ToUtc, q.Zone, q.Bucket);
        var events = await store.EventFactsAsync(q.FromUtc, q.ToUtc, ct);
        var posted = await store.PostedFactsAsync(q.FromUtc, q.ToUtc, ct);
        var times = await store.PostTimeFactsAsync(q.FromUtc, q.ToUtc, ct);

        IReadOnlyList<EventFact>? previousEvents = null;
        IReadOnlyList<PostedFact>? previousPosted = null;
        if (q.Compare is var (compareFrom, compareTo))
        {
            previousEvents = await store.EventFactsAsync(compareFrom, compareTo, ct);
            previousPosted = await store.PostedFactsAsync(compareFrom, compareTo, ct);
        }

        var summary = DashboardSummaryBuilder.Build(buckets, events, posted, times, previousEvents, previousPosted);
        var range = new SummaryRangeView(Render(q.FromUtc, q.Zone), Render(q.ToUtc, q.Zone), q.ZoneId, BucketName(q.Bucket));
        return Results.Json(SummaryView.Of(range, summary), Json);
    }

    // ---- the query (spec 4.1) ---------------------------------------------------------------------------------------

    private sealed record SummaryQuery(
        DateTime FromUtc, DateTime ToUtc, (DateTime From, DateTime To)? Compare, BucketSize Bucket, TimeZoneInfo Zone, string ZoneId);

    private static bool TryParse(IQueryCollection q, out SummaryQuery? query, out string? error)
    {
        query = null;
        error = null;
        foreach (var name in SingleValued)
        {
            if (q[name].Count > 1)
            {
                error = $"{name} may be given once.";
                return false;
            }
        }

        if (!TryInstant(q, "from", out var from) || !TryInstant(q, "to", out var to)
            || !TryInstant(q, "compareFrom", out var compareFrom) || !TryInstant(q, "compareTo", out var compareTo))
        {
            error = "from, to, compareFrom and compareTo are ISO 8601 instants with an offset, such as 2026-10-01T00:00:00Z "
                + "or 2026-10-01T00:00:00+05:30 (a + in a URL is written %2B).";
            return false;
        }

        if (from is null || to is null)
        {
            error = "from and to are required.";
            return false;
        }

        if (compareFrom is null != compareTo is null)
        {
            error = "compareFrom and compareTo are sent together or not at all.";
            return false;
        }

        if (!TryRange(from.Value, to.Value, "from", "to", out error)
            || (compareFrom is { } cf && !TryRange(cf, compareTo!.Value, "compareFrom", "compareTo", out error)))
        {
            return false;
        }

        var bucket = BucketSize.Day;
        if (q.TryGetValue("bucket", out var bucketText))
        {
            switch (bucketText.ToString())
            {
                case "day": bucket = BucketSize.Day; break;
                case "week": bucket = BucketSize.Week; break;
                default:
                    error = "bucket must be day or week.";
                    return false;
            }
        }

        // A tz that is sent must name a zone: blank or padded is refused, never trimmed or read as UTC.
        var zoneId = q.TryGetValue("tz", out var tzText) ? tzText.ToString() : "UTC";
        if (!V2QueryParser.IsZoneId(zoneId) || !DateBounds.TryZone(zoneId, out var zone))
        {
            error = "tz is an IANA time zone id, such as Asia/Kolkata or UTC.";
            return false;
        }

        query = new SummaryQuery(
            from.Value, to.Value, compareFrom is { } f ? (f, compareTo!.Value) : null, bucket, zone!, zoneId);
        return true;
    }

    private static bool TryRange(DateTime from, DateTime to, string fromName, string toName, out string? error)
    {
        error = null;
        if (to <= from)
        {
            error = $"{toName} must be after {fromName}.";
            return false;
        }

        if (to - from > TimeSpan.FromDays(MaxRangeDays))
        {
            error = $"{fromName} to {toName} may span at most {MaxRangeDays} days.";
            return false;
        }

        return true;
    }

    /// <summary>An instant that names its offset, as UTC (Kind Utc, which the stores require). Absent is fine.</summary>
    private static bool TryInstant(IQueryCollection q, string name, out DateTime? value)
    {
        value = null;
        if (!q.TryGetValue(name, out var text))
        {
            return true;
        }

        var raw = text.ToString();
        if (!Instant.IsMatch(raw) || !DateBounds.TryParse(raw, null, upper: false, out var utc))
        {
            return false;
        }

        value = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return true;
    }

    // ---- the response (spec 4.2) ------------------------------------------------------------------------------------

    /// <summary>The instant in <paramref name="zone"/>, with its offset: <c>2026-09-29T00:00:00+05:30</c>; fractions only when sent.</summary>
    private static string Render(DateTime utc, TimeZoneInfo zone)
    {
        var offset = zone.GetUtcOffset(utc);
        var local = new DateTimeOffset(DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified), offset);
        return local.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
    }

    private static string BucketName(BucketSize bucket) => bucket == BucketSize.Week ? "week" : "day";
}

public sealed record SummaryRangeView(string From, string To, string Tz, string Bucket);

public sealed record SummaryCountsView(int Received, int Posted, int PostFailed, int Discarded)
{
    public static SummaryCountsView? Of(Counts? c) => c is null ? null : new(c.Received, c.Posted, c.PostFailed, c.Discarded);
}

/// <summary>Money is written as a JSON number with two decimals (<see cref="MoneyJsonConverter"/>), never through double.</summary>
public sealed record SummaryPostedView(
    int Rows,
    [property: JsonConverter(typeof(MoneyJsonConverter))] decimal Amount,
    int? PreviousRows,
    [property: JsonConverter(typeof(NullableMoneyJsonConverter))] decimal? PreviousAmount);

public sealed record SummaryTimeToPostView(int MedianMinutes, int P90Minutes, int Statements);

public sealed record SummarySeriesView(string Start, int Received, int Posted, int PostFailed);

/// <summary>Spec 4.2, in its key order.</summary>
public sealed record SummaryView(
    SummaryRangeView Range,
    SummaryCountsView Counts,
    SummaryCountsView? Previous,
    SummaryPostedView Posted,
    SummaryTimeToPostView? TimeToPost,
    IReadOnlyList<SummarySeriesView> Series)
{
    public static SummaryView Of(SummaryRangeView range, DashboardSummary s) => new(
        range,
        SummaryCountsView.Of(s.Counts)!,
        SummaryCountsView.Of(s.Previous),
        new SummaryPostedView(s.Posted.Rows, s.Posted.Amount, s.Posted.PreviousRows, s.Posted.PreviousAmount),
        s.TimeToPost is { } t ? new SummaryTimeToPostView(t.MedianMinutes, t.P90Minutes, t.Statements) : null,
        s.Series.Select(e => new SummarySeriesView(
            e.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.Received, e.Posted, e.PostFailed)).ToList());
}

/// <summary>
/// A <see cref="decimal"/> as a JSON number with at least two decimals, invariant (<c>60.00</c>, <c>248391.27</c>).
/// Never rounded: stored amounts have 2 decimals, so 2 are written; a value carrying more keeps them (rule 3).
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<decimal>
{
    internal const string Format = "0.00############################";

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.ToString(Format, CultureInfo.InvariantCulture), skipInputValidation: true);
}

/// <inheritdoc cref="MoneyJsonConverter"/>
public sealed class NullableMoneyJsonConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteRawValue(v.ToString(MoneyJsonConverter.Format, CultureInfo.InvariantCulture), skipInputValidation: true);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
