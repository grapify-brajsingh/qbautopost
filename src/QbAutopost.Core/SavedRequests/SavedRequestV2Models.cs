namespace QbAutopost.Core.SavedRequests;

/// <summary>
/// The v2 list query (spec saved-requests-dashboard-v2 9.2): v1's parameters plus these, all combined with AND.
/// <see cref="SavedRequestQuery.NeedsAttention"/> is evaluated against <c>needs_attention_v2</c>, and
/// <see cref="SavedRequestQuery.Q"/> also matches the email subject and the push id prefix (spec 5.2).
/// <see cref="SavedRequestQuery.Sort"/> also accepts <c>received</c>, <c>total</c> and <c>rows</c>.
/// </summary>
/// <param name="ReceivedFrom">Inclusive, compared with <c>coalesce(email_received_utc, created_utc)</c> as a UTC date.</param>
/// <param name="ReceivedTo">Inclusive, as <paramref name="ReceivedFrom"/>.</param>
/// <param name="EmailFrom">Contains, case-insensitive.</param>
/// <param name="Reference">Exact.</param>
/// <param name="MinTotal">Inclusive; a request with no control total is excluded when either bound is given.</param>
/// <param name="MaxTotal">Inclusive, as <paramref name="MinTotal"/>.</param>
/// <param name="HasErrors"><c>problem_count &gt; 0 OR needs_attention_v2</c>; false is the negation.</param>
public sealed record SavedRequestQueryV2(
    SavedRequestQuery Base,
    DateOnly? ReceivedFrom,
    DateOnly? ReceivedTo,
    string? EmailFrom,
    string? Reference,
    decimal? MinTotal,
    decimal? MaxTotal,
    bool? HasErrors);

/// <summary>A flag counted among pending requests and among all requests (spec 9.3).</summary>
public sealed record FlagCounts(int Pending, int All);

/// <summary>v2 stats (spec 9.3). Every flag uses the v2 columns; <see cref="ByStatus"/> names every status.</summary>
public sealed record SavedRequestStatsV2(
    int Total,
    IReadOnlyDictionary<SavedRequestStatus, int> ByStatus,
    FlagCounts NeedsAttention,
    FlagCounts Duplicate,
    FlagCounts HasErrors);

/// <summary>Another not-yet-posted request with the same reference (spec 9.1, <c>otherUnpostedPushes</c>).</summary>
/// <param name="ReceivedUtc"><c>coalesce(email_received_utc, created_utc)</c>.</param>
public sealed record OtherPush(long Id, Guid? PushId, DateTime ReceivedUtc, SavedRequestStatus Status)
{
    public string PublicRef => $"SR-{Id:000000}";
}
