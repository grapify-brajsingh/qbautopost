using QbAutopost.Core.Dashboard;
using QbAutopost.Core.SavedRequests.Audit;
using Xunit;

namespace QbAutopost.Core.Tests.Dashboard;

public class DashboardSummaryBuilderTests
{
    private static readonly DateTime From = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<TimeBucket> Buckets() => TimeBuckets.Build(From, To, TimeZoneInfo.Utc, BucketSize.Day);

    private static string Result(params decimal[] amounts) =>
        "{\"posted\":[" + string.Join(',', amounts.Select((a, i) =>
            $"{{\"index\":{i},\"txnId\":\"T{i}\",\"amount\":{a.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}")) + "]}";

    [Fact]
    public void Should_CountOnlyPostedEvents_When_PartialAttemptPresent()
    {
        EventFact[] events =
        [
            new(AuditActions.Posted, From.AddHours(5)),
            new(AuditActions.PostFailed, From.AddHours(6)), // the partial attempt's event
        ];
        PostedFact[] posted =
        [
            new(1, From.AddHours(5), Result(10.00m, 5.25m)),
            new(2, From.AddHours(6), Result(100.10m)),
        ];

        var summary = DashboardSummaryBuilder.Build(Buckets(), events, posted, []);

        Assert.Equal(1, summary.Counts.Posted);
        Assert.Equal(1, summary.Counts.PostFailed);
        Assert.Equal(3, summary.Posted.Rows);
        Assert.Equal(115.35m, summary.Posted.Amount);
    }

    [Fact]
    public void Should_SumSeriesToCounts_When_Built()
    {
        EventFact[] events =
        [
            new(AuditActions.Received, From), new(AuditActions.Received, From.AddHours(30)),
            new(AuditActions.Received, To.AddTicks(-1)), new(AuditActions.Posted, From.AddHours(26)),
            new(AuditActions.PostFailed, From.AddHours(50)), new(AuditActions.Discarded, From.AddHours(51)),
        ];

        var summary = DashboardSummaryBuilder.Build(Buckets(), events, [], []);

        Assert.Equal(new Counts(3, 1, 1, 1), summary.Counts);
        Assert.Equal(3, summary.Series.Count);
        Assert.Equal(new[] { new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 28) },
            summary.Series.Select(s => s.Start));
        Assert.Equal(summary.Counts.Received, summary.Series.Sum(s => s.Received));
        Assert.Equal(summary.Counts.Posted, summary.Series.Sum(s => s.Posted));
        Assert.Equal(summary.Counts.PostFailed, summary.Series.Sum(s => s.PostFailed));
        Assert.Equal(new SeriesEntry(new DateOnly(2026, 10, 27), 1, 1, 0), summary.Series[1]);
    }

    [Fact]
    public void Should_ReturnNullTimeToPost_When_NoPosts()
    {
        var summary = DashboardSummaryBuilder.Build(Buckets(), [], [], []);

        Assert.Null(summary.TimeToPost);
    }

    [Fact]
    public void Should_ComputeMedianAndP90_When_Posts()
    {
        PostTimeFact[] times =
        [
            new(3, From, From.AddMinutes(600).AddSeconds(59)),
            new(1, From, From.AddMinutes(60)),
            new(2, From.AddHours(1), From.AddHours(1).AddMinutes(120).AddSeconds(30)),
        ];

        var summary = DashboardSummaryBuilder.Build(Buckets(), [], [], times);

        Assert.Equal(new TimeToPost(120, 600, 3), summary.TimeToPost);
    }

    [Fact]
    public void Should_ReturnNullPrevious_When_NoCompareRange()
    {
        var summary = DashboardSummaryBuilder.Build(Buckets(), [new(AuditActions.Received, From)], [new(1, From, Result(1m))], []);

        Assert.Null(summary.Previous);
        Assert.Null(summary.Posted.PreviousRows);
        Assert.Null(summary.Posted.PreviousAmount);

        var compared = DashboardSummaryBuilder.Build(
            Buckets(), [], [], [],
            [new(AuditActions.Received, From.AddDays(-1)), new(AuditActions.Discarded, From.AddDays(-1))],
            [new(9, From.AddDays(-1), Result(2.50m, 2.50m))], []);

        Assert.Equal(new Counts(1, 0, 0, 1), compared.Previous);
        Assert.Equal(2, compared.Posted.PreviousRows);
        Assert.Equal(5.00m, compared.Posted.PreviousAmount);
    }
}
