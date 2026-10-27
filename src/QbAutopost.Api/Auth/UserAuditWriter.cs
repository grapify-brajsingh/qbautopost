using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QbAutopost.Core.Abstractions;
using QbAutopost.Data;
using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Auth;

/// <summary>
/// Saved-requests spec 5.2, 11.2, 12: one <c>user_audit</c> row per sign-in event, written through
/// <see cref="QbAutopostDbContext"/> directly. <c>detail</c> carries the address and a reason, and the attempted email
/// when nobody holds it (R10); never a password or a token.
/// </summary>
public sealed class UserAuditWriter(IDbContextFactory<QbAutopostDbContext> contexts, IClock clock)
{
    public const string Login = "login";
    public const string LoginFailed = "login-failed";
    public const string LockedOut = "locked-out";
    public const string PasswordChanged = "password-changed";

    /// <summary>The actor of an event nobody signed in for: a failed or locked-out sign-in.</summary>
    public const string Anonymous = "anonymous";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(string? userId, string actor, string kind, object detail, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.UserAudit.Add(new UserAuditEntity
        {
            UserId = userId,
            Actor = actor,
            Kind = kind,
            Detail = JsonSerializer.Serialize(detail, Json),
            AtUtc = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
