using System.Collections.Concurrent;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Endpoints;

/// <summary>
/// T-901 (api-v1 §9): decides when a flat-path request is worth a warning — at most once per route per hour. The
/// owner needs to see which callers still use the old paths before Q-53 turns them off, but <c>start-all.ps1</c>
/// polls <c>/health/hermes</c>, so warning on every request would bury the log.
/// </summary>
public sealed class LegacyRouteLog(IClock clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTime> _lastWarned = new(StringComparer.Ordinal);

    /// <summary>
    /// True when this route has not been warned about within the last hour. Keyed by the route pattern, not the
    /// request path, so <c>/jobs/a</c> and <c>/jobs/b</c> count as one route.
    /// </summary>
    public bool ShouldWarn(string routePattern)
    {
        var now = clock.UtcNow;
        var warn = false;
        _lastWarned.AddOrUpdate(
            routePattern,
            _ =>
            {
                warn = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last < Window)
                {
                    return last;
                }

                warn = true;
                return now;
            });
        return warn;
    }
}
