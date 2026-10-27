using System.Net;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Hardening;

/// <summary>
/// T-911 / FR-A-15, last row: the brute-force brake. A key is long and random, so guessing it needs a great many
/// tries — the brake's job is to make those tries cost wall-clock time, not to make the key stronger.
/// <para>
/// It counts by address, not by key, because a guesser changes the key every time and keeps the address. Time comes
/// from <see cref="FixedClock"/>: a test that proved "blocked for five minutes" by waiting five minutes would never
/// be run again.
/// </para>
/// </summary>
public sealed class AuthBrakeTests
{
    private static readonly IPAddress Guesser = IPAddress.Parse("10.0.0.9");
    private static readonly IPAddress Innocent = IPAddress.Parse("10.0.0.10");

    [Fact]
    public void Should_Admit_When_FailuresStayUnderTheLimit()
    {
        var (brake, _) = Brake();

        for (var i = 0; i < 3; i++)
        {
            brake.RecordFailure(Guesser);
        }

        Assert.False(brake.IsBlocked(Guesser, out _));
    }

    [Fact]
    public void Should_Block_When_FailuresPassTheLimit()
    {
        var (brake, _) = Brake();

        for (var i = 0; i < 4; i++)
        {
            brake.RecordFailure(Guesser);
        }

        Assert.True(brake.IsBlocked(Guesser, out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(5), retryAfter);
    }

    [Fact]
    public void Should_BlockOnlyTheGuesser_When_AnotherAddressIsInnocent()
    {
        var (brake, _) = Brake();

        for (var i = 0; i < 4; i++)
        {
            brake.RecordFailure(Guesser);
        }

        Assert.True(brake.IsBlocked(Guesser, out _));
        Assert.False(brake.IsBlocked(Innocent, out _));
    }

    [Fact]
    public void Should_Admit_When_TheBlockHasExpired()
    {
        var (brake, clock) = Brake();
        for (var i = 0; i < 4; i++)
        {
            brake.RecordFailure(Guesser);
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(5).AddSeconds(1);

        Assert.False(brake.IsBlocked(Guesser, out _));
    }

    [Fact]
    public void Should_CountAfresh_When_TheMinuteHasPassed()
    {
        var (brake, clock) = Brake();
        for (var i = 0; i < 3; i++)
        {
            brake.RecordFailure(Guesser);
        }

        // Three failures a minute for ever is a misconfigured client, not an attack; it must never be blocked.
        clock.UtcNow = clock.UtcNow.AddSeconds(61);
        for (var i = 0; i < 3; i++)
        {
            brake.RecordFailure(Guesser);
        }

        Assert.False(brake.IsBlocked(Guesser, out _));
    }

    [Fact]
    public void Should_ShrinkTheRetryAfter_As_TheBlockRunsDown()
    {
        var (brake, clock) = Brake();
        for (var i = 0; i < 4; i++)
        {
            brake.RecordFailure(Guesser);
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(4);

        Assert.True(brake.IsBlocked(Guesser, out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);
    }

    [Fact]
    public void Should_Forget_When_TheCallerSucceeds()
    {
        var (brake, _) = Brake();
        for (var i = 0; i < 3; i++)
        {
            brake.RecordFailure(Guesser);
        }

        brake.RecordSuccess(Guesser);
        brake.RecordFailure(Guesser);

        // A caller who typed one key wrong, fixed it, then fat-fingered it again is not a guesser.
        Assert.False(brake.IsBlocked(Guesser, out _));
    }

    [Fact]
    public async Task Should_Answer429_When_AnAddressKeepsGuessingKeys()
    {
        using var factory = new ApiFactory();
        using var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Api:RateLimits:Enabled"] = "true",
            ["Api:RateLimits:FailedAuthPerMinute"] = "2",
        });
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "not-the-key");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.GetAsync("/api/v1/jobs");
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[3]);
    }

    private static (AuthBrake Brake, FixedClock Clock) Brake()
    {
        var clock = new FixedClock { UtcNow = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc) };
        var limits = new RateLimitSettings { FailedAuthPerMinute = 3, FailedAuthBlockMinutes = 5 };
        return (new AuthBrake(clock, limits), clock);
    }
}
