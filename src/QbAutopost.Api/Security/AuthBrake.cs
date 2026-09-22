using System.Collections.Concurrent;
using System.Net;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Security;

/// <summary>
/// FR-A-15, last row: the brute-force brake. After <see cref="RateLimitSettings.FailedAuthPerMinute"/> failed
/// authentications in a minute, an address is refused outright for
/// <see cref="RateLimitSettings.FailedAuthBlockMinutes"/> minutes.
/// <para>
/// It counts by address, because a guesser changes the key every attempt and keeps the address, and it counts only
/// <b>unrecognised keys</b>: a caller whose key is valid but whose scope or network is not has not failed to
/// authenticate, and must not be able to lock itself out by calling one route it was never granted.
/// </para>
/// <para>
/// State is in memory and therefore per process: a restart forgives everyone. That is the right trade for a brake
/// whose job is to make guessing slow — persisting it would add a file to keep, prune and back up for no real gain.
/// </para>
/// </summary>
public sealed class AuthBrake(IClock clock, RateLimitSettings limits)
{
    private readonly ConcurrentDictionary<string, Attempts> _byAddress = new();

    /// <summary>True when this address is serving a block; <paramref name="retryAfter"/> is what is left of it.</summary>
    public bool IsBlocked(IPAddress? address, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!limits.Enabled || !_byAddress.TryGetValue(Key(address), out var attempts) || attempts.BlockedUntil is not { } until)
        {
            return false;
        }

        var now = clock.UtcNow;
        if (until <= now)
        {
            // The block is spent. Forget the address entirely, so the next mistake starts a fresh count rather than
            // tipping straight back over the edge.
            _byAddress.TryRemove(Key(address), out _);
            return false;
        }

        retryAfter = until - now;
        return true;
    }

    /// <summary>Records a request that arrived with a key belonging to nobody.</summary>
    public void RecordFailure(IPAddress? address)
    {
        if (!limits.Enabled)
        {
            return;
        }

        var now = clock.UtcNow;
        _byAddress.AddOrUpdate(
            Key(address),
            _ => new Attempts(1, now, null),
            (_, existing) =>
            {
                if (existing.BlockedUntil is { } until && until > now)
                {
                    return existing;
                }

                // Outside the minute the count started in, this is the first failure of a new minute.
                var count = now - existing.WindowStart > TimeSpan.FromMinutes(1) ? 1 : existing.Count + 1;
                var start = count == 1 ? now : existing.WindowStart;
                return count > limits.FailedAuthPerMinute
                    ? new Attempts(count, start, now.AddMinutes(limits.FailedAuthBlockMinutes))
                    : new Attempts(count, start, null);
            });
    }

    /// <summary>
    /// Records a key that resolved to a caller. The count is cleared: somebody who mistyped a key, fixed it, then
    /// mistyped it again is not guessing, and should not accumulate their way into a block over a working day.
    /// </summary>
    public void RecordSuccess(IPAddress? address) => _byAddress.TryRemove(Key(address), out _);

    private static string Key(IPAddress? address) => address?.ToString() ?? "unknown";

    private sealed record Attempts(int Count, DateTime WindowStart, DateTime? BlockedUntil);
}
