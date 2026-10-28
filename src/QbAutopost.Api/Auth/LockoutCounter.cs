using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QbAutopost.Core.Abstractions;
using QbAutopost.Data;

namespace QbAutopost.Api.Auth;

/// <summary>
/// T-1010 fix 1a (spec 11.2): Identity lockout that holds under parallel wrong passwords. <c>AccessFailedAsync</c>
/// writes a count read earlier and loses to the concurrency stamp, so ten racing failures could count as one. Here the
/// increment, and the lockout once the post-increment count reaches <c>MaxFailedAccessAttempts</c>, are one
/// <c>UPDATE</c>; PostgreSQL serialises it per row. As Identity does, the count restarts at zero once the account
/// locks, and a locked account is not counted further. The concurrency stamp is rotated, so a stale
/// <c>UpdateAsync</c> elsewhere fails rather than writing an old count back.
/// </summary>
public sealed class LockoutCounter(IDbContextFactory<QbAutopostDbContext> contexts, IOptions<IdentityOptions> identity, IClock clock)
{
    /// <summary>Counts one failure for <paramref name="userId"/>; returns when the account is now locked until, or null.</summary>
    public async Task<DateTimeOffset?> RecordFailureAsync(string userId, CancellationToken ct)
    {
        var lockout = identity.Value.Lockout;
        var now = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero);
        var end = now.Add(lockout.DefaultLockoutTimeSpan);
        var max = lockout.MaxFailedAccessAttempts;
        var stamp = Guid.NewGuid().ToString();

        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE users SET
                access_failed_count = CASE WHEN access_failed_count + 1 >= {max} THEN 0 ELSE access_failed_count + 1 END,
                lockout_end = CASE WHEN access_failed_count + 1 >= {max} THEN {end} ELSE lockout_end END,
                concurrency_stamp = {stamp}
            WHERE id = {userId} AND lockout_enabled AND (lockout_end IS NULL OR lockout_end <= {now})
            """,
            ct);
        var lockedUntil = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.LockoutEnd).SingleOrDefaultAsync(ct);
        return lockedUntil > now ? lockedUntil : null;
    }
}
