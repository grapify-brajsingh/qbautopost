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
/// <para>
/// T-1010 (saved-requests spec 11.2): sign-in is braked per address and per email on the same counts and limits. An
/// email is counted under <see cref="EmailKey"/>, so it never shares a count with an address.
/// </para>
/// </summary>
public sealed class AuthBrake(IClock clock, RateLimitSettings limits)
{
    private readonly ConcurrentDictionary<string, Attempts> _byAddress = new();

    /// <summary>True when this address is serving a block; <paramref name="retryAfter"/> is what is left of it.</summary>
    public bool IsBlocked(IPAddress? address, out TimeSpan retryAfter) => IsBlocked(Key(address), out retryAfter);

    /// <summary>The key one email is counted under: case-insensitive, as Identity matches emails.</summary>
    public static string EmailKey(string email) => "email:" + email.Trim().ToUpperInvariant();

    /// <summary>True when <paramref name="key"/> (an address, or an <see cref="EmailKey"/>) is serving a block.</summary>
    public bool IsBlocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!limits.Enabled || !_byAddress.TryGetValue(key, out var attempts) || attempts.BlockedUntil is not { } until)
        {
            return false;
        }

        var now = clock.UtcNow;
        if (until <= now)
        {
            // The block is spent. Forget the address entirely, so the next mistake starts a fresh count rather than
            // tipping straight back over the edge.
            _byAddress.TryRemove(key, out _);
            return false;
        }

        retryAfter = until - now;
        return true;
    }

    /// <summary>Records a request that arrived with a key belonging to nobody.</summary>
    public void RecordFailure(IPAddress? address) => RecordFailure(Key(address));

    /// <summary>Records a failed authentication counted under <paramref name="key"/>.</summary>
    public void RecordFailure(string key)
    {
        if (!limits.Enabled)
        {
            return;
        }

        var now = clock.UtcNow;
        _byAddress.AddOrUpdate(
            key,
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
    /// T-1010 fix 1b: counts a failure for <paramref name="key"/> <b>before</b> the slow credential check, as one atomic
    /// step with the block test, so a parallel burst cannot all pass the check and then fail together. False when the
    /// key is already blocked (nothing is counted); true otherwise, and this reservation may itself trip the block,
    /// exactly as <see cref="RecordFailure(string)"/> would. A sign-in that then succeeds gives it back with <see cref="Release(string)"/>.
    /// </summary>
    public bool TryReserve(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!limits.Enabled)
        {
            return true;
        }

        while (true)
        {
            var now = clock.UtcNow;
            if (!_byAddress.TryGetValue(key, out var existing))
            {
                if (_byAddress.TryAdd(key, Next(null, now)))
                {
                    return true;
                }

                continue;
            }

            if (existing.BlockedUntil is { } until && until > now)
            {
                retryAfter = until - now;
                return false;
            }

            if (_byAddress.TryUpdate(key, Next(existing, now), existing))
            {
                return true;
            }
        }
    }

    public bool TryReserve(IPAddress? address, out TimeSpan retryAfter) => TryReserve(Key(address), out retryAfter);

    /// <summary>Gives back one <see cref="TryReserve(string, out TimeSpan)"/>, lifting a block the count no longer reaches.</summary>
    public void Release(string key)
    {
        while (_byAddress.TryGetValue(key, out var existing))
        {
            if (existing.Count <= 1)
            {
                if (_byAddress.TryRemove(new KeyValuePair<string, Attempts>(key, existing)))
                {
                    return;
                }

                continue;
            }

            var count = existing.Count - 1;
            var next = existing with { Count = count, BlockedUntil = count > limits.FailedAuthPerMinute ? existing.BlockedUntil : null };
            if (_byAddress.TryUpdate(key, next, existing))
            {
                return;
            }
        }
    }

    public void Release(IPAddress? address) => Release(Key(address));

    /// <summary>
    /// Records a key that resolved to a caller. The count is cleared: somebody who mistyped a key, fixed it, then
    /// mistyped it again is not guessing, and should not accumulate their way into a block over a working day.
    /// </summary>
    public void RecordSuccess(IPAddress? address) => RecordSuccess(Key(address));

    /// <summary>Clears the count kept under <paramref name="key"/>.</summary>
    public void RecordSuccess(string key) => _byAddress.TryRemove(key, out _);

    /// <summary>The count after one more failure at <paramref name="now"/>, blocked once it passes the limit.</summary>
    private Attempts Next(Attempts? existing, DateTime now)
    {
        var fresh = existing is null || now - existing.WindowStart > TimeSpan.FromMinutes(1);
        var count = fresh ? 1 : existing!.Count + 1;
        var start = fresh ? now : existing!.WindowStart;
        return count > limits.FailedAuthPerMinute
            ? new Attempts(count, start, now.AddMinutes(limits.FailedAuthBlockMinutes))
            : new Attempts(count, start, null);
    }

    private static string Key(IPAddress? address) => address?.ToString() ?? "unknown";

    private sealed record Attempts(int Count, DateTime WindowStart, DateTime? BlockedUntil);
}
