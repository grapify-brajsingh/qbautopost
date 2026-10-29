using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Core.Dashboard;

/// <summary>Builds the summary (spec dashboard-api-m12 4.2) from store facts. It only counts and sums stored values.</summary>
public static class DashboardSummaryBuilder
{
    /// <summary>
    /// <paramref name="buckets"/> cover the range (from <see cref="TimeBuckets.Build"/>); the facts are the store's for
    /// the range, and the previous ones are for the compare range, or null when none was sent. Previous post times are
    /// accepted for symmetry; the response has no previous time-to-post.
    /// </summary>
    public static DashboardSummary Build(
        IReadOnlyList<TimeBucket> buckets,
        IReadOnlyList<EventFact> events,
        IReadOnlyList<PostedFact> posted,
        IReadOnlyList<PostTimeFact> times,
        IReadOnlyList<EventFact>? previousEvents = null,
        IReadOnlyList<PostedFact>? previousPosted = null,
        IReadOnlyList<PostTimeFact>? previousTimes = null)
    {
        var (rows, amount) = Money(posted);
        (int Rows, decimal Amount)? previous = previousPosted is null ? null : Money(previousPosted);

        return new DashboardSummary(
            CountsOf(events),
            previousEvents is null ? null : CountsOf(previousEvents),
            new Posted(rows, amount, previous?.Rows, previous?.Amount),
            TimeToPostOf(times),
            SeriesOf(buckets, events));
    }

    private static Counts CountsOf(IReadOnlyList<EventFact> events) => new(
        events.Count(e => e.Action == AuditActions.Received),
        events.Count(e => e.Action == AuditActions.Posted),
        events.Count(e => e.Action == AuditActions.PostFailed),
        events.Count(e => e.Action == AuditActions.Discarded));

    private static (int Rows, decimal Amount) Money(IReadOnlyList<PostedFact> posted)
    {
        var rows = 0;
        var amount = 0m;
        foreach (var fact in posted)
        {
            var (r, a) = AttemptResultRows.Read(fact.ResultJson);
            rows += r;
            amount += a;
        }
        return (rows, amount);
    }

    /// <summary>
    /// Ruling R17 (tracker Q-115): a statement whose start is after its post time (a client-sent <c>email.receivedUtc</c>
    /// in the future) is left out, and <c>statements</c> counts only the included ones; null when none is left.
    /// </summary>
    private static TimeToPost? TimeToPostOf(IReadOnlyList<PostTimeFact> times)
    {
        var included = times.Where(t => t.ReceivedUtc <= t.PostedUtc).ToList();
        if (included.Count == 0) return null;
        var minutes = included
            .Select(t => (int)Math.Floor((t.PostedUtc - t.ReceivedUtc).TotalMinutes))
            .Order()
            .ToList();
        return new TimeToPost(Percentiles.NearestRank(minutes, 50), Percentiles.NearestRank(minutes, 90), minutes.Count);
    }

    private static IReadOnlyList<SeriesEntry> SeriesOf(IReadOnlyList<TimeBucket> buckets, IReadOnlyList<EventFact> events)
    {
        var received = new int[buckets.Count];
        var posted = new int[buckets.Count];
        var failed = new int[buckets.Count];
        foreach (var e in events)
        {
            var i = TimeBuckets.IndexOf(buckets, e.AtUtc);
            if (i < 0) continue;
            switch (e.Action)
            {
                case AuditActions.Received: received[i]++; break;
                case AuditActions.Posted: posted[i]++; break;
                case AuditActions.PostFailed: failed[i]++; break;
            }
        }
        return buckets.Select((b, i) => new SeriesEntry(b.Start, received[i], posted[i], failed[i])).ToList();
    }
}
