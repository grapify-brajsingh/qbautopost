using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Security;
using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Auth;

/// <summary>Turns a validated access token into the caller it belongs to (saved-requests spec 10.11, 11.3).</summary>
public interface IUserGate
{
    /// <summary>The user, with their role's scopes; null when they are unknown, disabled, or the stamp no longer matches.</summary>
    Task<Caller?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct);
}

/// <summary>
/// Spec 10.11: every Bearer request checks that the user is enabled and that the token's <c>stamp</c> is Identity's
/// current security stamp. The lookup is cached per user id for <see cref="CacheFor"/>, so a disabled user keeps access
/// for at most that long. The age is measured with <see cref="IClock"/>, not the cache's own clock, so it is testable.
/// <para>
/// A token whose stamp differs from the cached one is looked up again once before it is refused: after a password
/// change the user's new token carries the new stamp while the cache may still hold the old one.
/// </para>
/// </summary>
public sealed class UserGate(UserManager<AppUser> users, IMemoryCache cache, IClock clock) : IUserGate
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public async Task<Caller?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = principal.FindFirstValue(AccessTokens.Subject);
        var stamp = principal.FindFirstValue(AccessTokens.Stamp);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(stamp))
        {
            return null;
        }

        var user = await SnapshotAsync(id, fresh: false);
        if (user is not null && !SameStamp(user.Stamp, stamp))
        {
            user = await SnapshotAsync(id, fresh: true);
        }

        if (user is null || user.Disabled || !SameStamp(user.Stamp, stamp))
        {
            return null;
        }

        return Caller.ForUser(user.Id, user.Name, user.Email, user.Role, user.MustChangePassword);
    }

    private async Task<UserSnapshot?> SnapshotAsync(string id, bool fresh)
    {
        var key = CacheKey(id);
        var now = clock.UtcNow;
        var generation = GenerationOf(id);
        if (!fresh && cache.TryGetValue(key, out CachedUser? cached) && cached is not null && now - cached.FetchedUtc < CacheFor)
        {
            return cached.User;
        }

        var snapshot = await LoadAsync(id);
        // R19: a Forget that ran while this was loading means the snapshot may predate the admin's change, so it is
        // used for this one request but not cached. Checked and set under the lock Forget takes.
        lock (Forgetting)
        {
            if (GenerationOf(id) == generation)
            {
                // The absolute expiry only frees memory; freshness is decided by FetchedUtc above.
                cache.Set(key, new CachedUser(snapshot, now), CacheFor);
            }
        }

        return snapshot;
    }

    private async Task<UserSnapshot?> LoadAsync(string id)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return null;
        }

        // SPEC-GAP T-1009 (Q-87). Hold, don't guess: a user with no role, or more than one, gets no scopes.
        var roles = await users.GetRolesAsync(user);
        var role = roles.Count == 1 ? roles[0] : "";
        var stamp = await users.GetSecurityStampAsync(user);
        return new UserSnapshot(
            user.Id,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? user.Id : user.DisplayName,
            user.Email,
            role,
            stamp,
            user.IsDisabled,
            user.MustChangePassword);
    }

    /// <summary>
    /// Drops the cached lookup of <paramref name="id"/>. Called after an admin changes a user's role, disables them or
    /// resets their password, so the next request sees the new stamp now and not up to <see cref="CacheFor"/> later.
    /// </summary>
    public static void Forget(IMemoryCache cache, string id)
    {
        lock (Forgetting)
        {
            Generations[id] = GenerationOf(id) + 1;
            cache.Remove(CacheKey(id));
        }
    }

    /// <summary>Bumped by <see cref="Forget"/>; a lookup that started before a bump does not cache its result.</summary>
    private static readonly Dictionary<string, long> Generations = [];

    private static readonly object Forgetting = new();

    private static long GenerationOf(string id)
    {
        lock (Forgetting)
        {
            return Generations.GetValueOrDefault(id);
        }
    }

    private static string CacheKey(string id) => "user-gate:" + id;

    private static bool SameStamp(string current, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(current)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

    private sealed record CachedUser(UserSnapshot? User, DateTime FetchedUtc);

    private sealed record UserSnapshot(
        string Id, string Name, string? Email, string Role, string Stamp, bool Disabled, bool MustChangePassword);
}
