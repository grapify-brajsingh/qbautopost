using QbAutopost.Core.Dashboard;
using Xunit;

namespace QbAutopost.Core.Tests.Dashboard;

/// <summary>
/// Ruling R17 (tracker Q-115): a statement whose start (email received, else created) is after its post time — a
/// client-supplied <c>email.receivedUtc</c> in the future — is left out of time-to-post instead of counting a negative wait.
/// </summary>
public class DashboardSummaryFutureStartTests
{
    private static readonly DateTime From = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<TimeBucket> Buckets() => TimeBuckets.Build(From, To, TimeZoneInfo.Utc, BucketSize.Day);

    [Fact]
    public void Should_ExcludeStatement_When_StartIsAfterPostTime()
    {
        PostTimeFact[] times =
        [
            new(1, From, From.AddMinutes(30)),
            new(2, From.AddHours(5), From.AddHours(1)), // starts 4 h after it was posted
        ];

        var summary = DashboardSummaryBuilder.Build(Buckets(), [], [], times);

        Assert.Equal(new TimeToPost(30, 30, 1), summary.TimeToPost);
    }

    [Fact]
    public void Should_ReturnNullTimeToPost_When_EveryStartIsAfterPostTime()
    {
        PostTimeFact[] times = [new(1, From.AddHours(2), From.AddHours(1))];

        var summary = DashboardSummaryBuilder.Build(Buckets(), [], [], times);

        Assert.Null(summary.TimeToPost);
    }

    [Fact]
    public void Should_KeepStatement_When_StartEqualsPostTime()
    {
        PostTimeFact[] times = [new(1, From.AddHours(1), From.AddHours(1))];

        var summary = DashboardSummaryBuilder.Build(Buckets(), [], [], times);

        Assert.Equal(new TimeToPost(0, 0, 1), summary.TimeToPost);
    }
}
