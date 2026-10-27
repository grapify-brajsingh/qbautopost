using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Routing;

/// <summary>
/// T-901 (api-v1 §9): a flat path still works but is warned about, "at most once per route per hour" — often enough
/// for the owner to see which callers must move before Q-53 turns the legacy routes off, rarely enough that a polling
/// script cannot bury the log.
/// </summary>
public sealed class LegacyRouteLogTests
{
    private readonly FixedClock _clock = new();

    private LegacyRouteLog Log => new(_clock);

    [Fact]
    public void Should_WarnOnce_When_SameRouteIsUsedTwiceWithinTheHour()
    {
        var log = Log;

        Assert.True(log.ShouldWarn("/jobs"));
        Assert.False(log.ShouldWarn("/jobs"));
    }

    [Fact]
    public void Should_WarnAgain_When_AnHourHasPassed()
    {
        var log = Log;
        Assert.True(log.ShouldWarn("/jobs"));

        _clock.UtcNow = _clock.UtcNow.AddHours(1).AddSeconds(1);

        Assert.True(log.ShouldWarn("/jobs"));
    }

    [Fact]
    public void Should_StayQuiet_When_TheHourHasNotQuitePassed()
    {
        var log = Log;
        Assert.True(log.ShouldWarn("/jobs"));

        _clock.UtcNow = _clock.UtcNow.AddMinutes(59);

        Assert.False(log.ShouldWarn("/jobs"));
    }

    [Fact]
    public void Should_WarnForEachRoute_When_DifferentRoutesAreUsed()
    {
        var log = Log;

        Assert.True(log.ShouldWarn("/jobs"));
        Assert.True(log.ShouldWarn("/health/hermes"));
        Assert.True(log.ShouldWarn("/batches/{id}/undo"));
        Assert.False(log.ShouldWarn("/jobs"));
    }
}
