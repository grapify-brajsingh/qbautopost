namespace QbAutopost.Core.Dashboard;

/// <summary>
/// The body of <c>GET /api/v2/dashboard/summary</c> (spec dashboard-api-m12 4.2) without <c>range</c>, which only echoes
/// the request and is added by the endpoint.
/// </summary>
/// <param name="Previous">Null when no compare range was sent.</param>
/// <param name="TimeToPost">Null when no statement has a post time in the range.</param>
public sealed record DashboardSummary(
    Counts Counts,
    Counts? Previous,
    Posted Posted,
    TimeToPost? TimeToPost,
    IReadOnlyList<SeriesEntry> Series);

/// <summary>The four audit totals; <see cref="PostFailed"/> includes partial attempts (Q-104).</summary>
public sealed record Counts(int Received, int Posted, int PostFailed, int Discarded);

/// <summary>
/// Money that reached QuickBooks: <c>posted[]</c> of the posted and partial attempts, a plain decimal sum of stored
/// values. The previous pair is null when no compare range was sent.
/// </summary>
public sealed record Posted(int Rows, decimal Amount, int? PreviousRows, decimal? PreviousAmount);

/// <summary>Nearest-rank minutes from received to posted, rounded down, over <see cref="Statements"/> requests.</summary>
public sealed record TimeToPost(int MedianMinutes, int P90Minutes, int Statements);

/// <summary>One chart bucket: its local start date and its counts.</summary>
public sealed record SeriesEntry(DateOnly Start, int Received, int Posted, int PostFailed);
