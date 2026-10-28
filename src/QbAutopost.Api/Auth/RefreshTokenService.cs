using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.Data;
using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Auth;

public enum RefreshOutcome
{
    /// <summary>The token was current: it is now revoked and <see cref="RefreshResult.NewToken"/> replaces it.</summary>
    Ok,

    /// <summary>No such token, or it has expired. Nothing changed.</summary>
    Invalid,

    /// <summary>A token that was already revoked came back: its whole family is now revoked (spec 11.2, theft signal).</summary>
    ReuseDetected,
}

/// <param name="FamilyId">
/// T-1115: the presented token's family, set by <see cref="RefreshTokenService.RotateWithGraceAsync"/> so a v2 refusal
/// can be audited as <c>refresh-reuse</c> with <c>{ familyId }</c>. <see cref="RefreshTokenService.RotateAsync"/> leaves it null.
/// </param>
public sealed record RefreshResult(RefreshOutcome Outcome, string? UserId, string? NewToken, Guid? FamilyId = null);

/// <summary>
/// Saved-requests spec 5.2, 11.2: refresh tokens are 32 random bytes, base64url, and only their SHA-256 (lower hex)
/// is stored. Each sign-in starts a family; each refresh revokes the presented token and issues its successor in the
/// same family. Writes go through <see cref="QbAutopostDbContext"/> directly: these are Identity concerns, not the
/// saved-request store's.
/// </summary>
public sealed class RefreshTokenService(IDbContextFactory<QbAutopostDbContext> contexts, IOptions<AppSettings> settings, IClock clock)
{
    private const int TokenBytes = 32;

    /// <summary>Issues a token for <paramref name="userId"/>, in <paramref name="familyId"/> or a new family; returns the raw token.</summary>
    public async Task<string> IssueAsync(string userId, Guid? familyId, string? ip, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (raw, _) = Add(db, userId, familyId ?? Guid.NewGuid(), ip, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>
    /// Rotates <paramref name="rawToken"/>. The revocation is a conditional update, so two refreshes racing with one
    /// token cannot both succeed: the loser finds it revoked and is treated as reuse.
    /// </summary>
    public async Task<RefreshResult> RotateAsync(string rawToken, string? ip, CancellationToken ct)
    {
        var hash = HashOf(rawToken);
        var now = clock.UtcNow;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var current = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenSha256 == hash, ct);
        if (current is null)
        {
            return new RefreshResult(RefreshOutcome.Invalid, null, null);
        }

        if (current.RevokedUtc is not null)
        {
            // M10 review: reuse is detected; revoking the family must not depend on the presenter staying connected.
            await RevokeFamilyAsync(db, current.FamilyId, now, CancellationToken.None);
            return new RefreshResult(RefreshOutcome.ReuseDetected, current.UserId, null);
        }

        if (current.ExpiresUtc <= now)
        {
            return new RefreshResult(RefreshOutcome.Invalid, current.UserId, null);
        }

        if (await RotateLiveAsync(db, current, ip, now, ct) is not { } raw)
        {
            await RevokeFamilyAsync(db, current.FamilyId, now, CancellationToken.None);
            return new RefreshResult(RefreshOutcome.ReuseDetected, current.UserId, null);
        }

        return new RefreshResult(RefreshOutcome.Ok, current.UserId, raw);
    }

    /// <summary>
    /// T-1115 (dashboard v2 spec 11): <see cref="RotateAsync"/> for the v2 refresh, plus the reuse grace. A revoked
    /// token is accepted once, and a second successor issued in its family, only when all of these hold:
    /// (a) it was revoked by rotation (it has a successor); (b) that was at most <paramref name="grace"/> ago;
    /// (c) its successor is neither rotated nor revoked (nor expired), which also means the family is not revoked,
    /// since revoking a family revokes every live token in it; (d) <paramref name="ip"/> equals the successor's
    /// <c>created_ip</c>; (e) no grace was given for it before (<c>grace_successor_id</c> is null). Anything else
    /// revokes the family, as v1 does.
    /// <para>
    /// The second successor is recorded with a conditional update (<c>grace_successor_id</c> still null), so two grace
    /// refreshes racing with one token give exactly one second successor; the loser takes the theft path. Once the
    /// presented token is known to be revoked, nothing here depends on the caller staying connected.
    /// </para>
    /// </summary>
    public async Task<RefreshResult> RotateWithGraceAsync(string rawToken, string? ip, TimeSpan grace, CancellationToken ct)
    {
        var hash = HashOf(rawToken);
        var now = clock.UtcNow;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var current = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenSha256 == hash, ct);
        if (current is null)
        {
            return new RefreshResult(RefreshOutcome.Invalid, null, null);
        }

        if (current.RevokedUtc is null)
        {
            if (current.ExpiresUtc <= now)
            {
                return new RefreshResult(RefreshOutcome.Invalid, current.UserId, null, current.FamilyId);
            }

            if (await RotateLiveAsync(db, current, ip, now, ct) is { } raw)
            {
                return new RefreshResult(RefreshOutcome.Ok, current.UserId, raw, current.FamilyId);
            }

            // A concurrent refresh rotated it first (and has committed: the claim waited for its row lock). Read what
            // that refresh stored, so the grace below sees the successor.
            current = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == current.Id, CancellationToken.None);
        }

        if (await GraceAsync(db, current, ip, now, grace) is { } second)
        {
            return new RefreshResult(RefreshOutcome.Ok, current.UserId, second, current.FamilyId);
        }

        await RevokeFamilyAsync(db, current.FamilyId, now, CancellationToken.None);
        return new RefreshResult(RefreshOutcome.ReuseDetected, current.UserId, null, current.FamilyId);
    }

    /// <summary>Revokes <paramref name="rawToken"/> and nothing else (logout); true when a live token was revoked.</summary>
    public async Task<bool> RevokeAsync(string rawToken, CancellationToken ct)
    {
        var hash = HashOf(rawToken);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var revoked = await db.RefreshTokens
            .Where(t => t.TokenSha256 == hash && t.RevokedUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedUtc, clock.UtcNow), ct);
        return revoked > 0;
    }

    /// <summary>Revokes every live token of <paramref name="userId"/> (disable, role change, reset: spec 10.11, 11.4).</summary>
    public Task RevokeAllAsync(string userId, CancellationToken ct) => RevokeOthersAsync(userId, keepRawToken: null, ct);

    /// <summary>Revokes every live token of <paramref name="userId"/> except <paramref name="keepRawToken"/> (change-password).</summary>
    public async Task RevokeOthersAsync(string userId, string? keepRawToken, CancellationToken ct)
    {
        var keep = keepRawToken is null ? null : HashOf(keepRawToken);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedUtc == null && (keep == null || t.TokenSha256 != keep))
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedUtc, clock.UtcNow), ct);
    }

    /// <summary>When a token issued now stops working; the cookie carries the same expiry.</summary>
    public DateTime ExpiresFrom(DateTime now) => now.AddDays(settings.Value.Auth.Jwt.RefreshTokenDays);

    /// <summary>Lower-hex SHA-256 of the raw token's ASCII: the only form ever stored.</summary>
    public static string HashOf(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(rawToken))).ToLowerInvariant();

    private (string Raw, RefreshTokenEntity Entity) Add(QbAutopostDbContext db, string userId, Guid familyId, string? ip, DateTime now)
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(TokenBytes));
        var entity = new RefreshTokenEntity
        {
            UserId = userId,
            TokenSha256 = HashOf(raw),
            FamilyId = familyId,
            CreatedUtc = now,
            ExpiresUtc = ExpiresFrom(now),
            CreatedIp = ip,
        };
        db.RefreshTokens.Add(entity);
        return (raw, entity);
    }

    /// <summary>
    /// Revokes the live token <paramref name="current"/> and issues its successor, in one transaction. The revocation is
    /// conditional, so of two refreshes racing with one token only one gets a successor; null for the loser.
    /// </summary>
    private async Task<string?> RotateLiveAsync(QbAutopostDbContext db, RefreshTokenEntity current, string? ip, DateTime now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == current.Id && t.RevokedUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedUtc, now), ct);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }

        var (raw, successor) = Add(db, current.UserId, current.FamilyId, ip, now);
        await db.SaveChangesAsync(ct);
        await db.RefreshTokens
            .Where(t => t.Id == current.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.ReplacedById, successor.Id), ct);
        await transaction.CommitAsync(ct);
        return raw;
    }

    /// <summary>
    /// The grace refresh of the revoked token <paramref name="current"/> (see <see cref="RotateWithGraceAsync"/>): the raw
    /// second successor, or null when any condition fails. Runs under <see cref="CancellationToken.None"/>.
    /// </summary>
    private async Task<string?> GraceAsync(QbAutopostDbContext db, RefreshTokenEntity current, string? ip, DateTime now, TimeSpan grace)
    {
        var none = CancellationToken.None;

        // (a) revoked by rotation, (b) within the grace, (e) no grace given yet.
        if (current.ReplacedById is not { } successorId
            || current.RevokedUtc is not { } revokedUtc
            || now - revokedUtc > grace
            || current.GraceSuccessorId is not null)
        {
            return null;
        }

        // SPEC-GAP T-1115 (Q-102): (d) compares addresses; an unknown address equals nothing, so it gets no grace.
        if (string.IsNullOrEmpty(ip))
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(none);

        // The successor's row lock: a rotation or revocation of it in flight finishes first, so the read below is final.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM refresh_tokens WHERE id = {successorId} FOR UPDATE", none);
        var successor = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == successorId, none);

        // (c) the successor is live: not rotated, not revoked, not expired. (d) the caller's address is the successor's.
        if (successor is null
            || successor.RevokedUtc is not null
            || successor.ReplacedById is not null
            || successor.ExpiresUtc <= now
            || !string.Equals(successor.CreatedIp, ip, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(none);
            return null;
        }

        var (raw, second) = Add(db, current.UserId, current.FamilyId, ip, now);
        await db.SaveChangesAsync(none);

        // (e) again, atomically: of two grace refreshes racing with one token, exactly one records its second successor.
        var recorded = await db.RefreshTokens
            .Where(t => t.Id == current.Id && t.GraceSuccessorId == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.GraceSuccessorId, second.Id), none);
        if (recorded == 0)
        {
            await transaction.RollbackAsync(none);
            return null;
        }

        await transaction.CommitAsync(none);
        return raw;
    }

    private static Task<int> RevokeFamilyAsync(QbAutopostDbContext db, Guid familyId, DateTime now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedUtc, now), ct);
}
