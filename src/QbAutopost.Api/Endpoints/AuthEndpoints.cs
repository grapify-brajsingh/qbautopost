using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Auth;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Api.Security;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Security;
using QbAutopost.Data;
using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Endpoints;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Spec 11.2, <c>GET /auth/me</c>, and the <c>user</c> of every sign-in answer.</summary>
public sealed record AuthUserView(
    string Id, string? Email, string DisplayName, string Role, bool MustChangePassword, IReadOnlyList<string> Scopes)
{
    public static AuthUserView Of(Caller caller) =>
        new(caller.Id, caller.Email, caller.Name, caller.Role ?? "", caller.MustChangePassword,
            caller.Scopes.Order(StringComparer.Ordinal).ToList());
}

public sealed record SignInResponse(string AccessToken, DateTime ExpiresUtc, AuthUserView User);

public sealed record SignedOutResponse(bool SignedOut);

/// <summary>
/// Saved-requests spec 11.2: dashboard sign-in. A short-lived JWT access token (<see cref="TokenService"/>) and a
/// rotating refresh token in an <c>HttpOnly; Secure; SameSite=Strict</c> cookie scoped to <see cref="CookiePath"/>
/// (<see cref="RefreshTokenService"/>). Served under <c>/api/v1</c> only: the cookie's path names that surface.
/// <para>
/// Login and refresh carry no credential, so <c>CallerMiddleware</c> passes them through; they are braked here (R15):
/// per address and per email with the failed-auth brake, on top of Identity's per-user lockout. Every failed sign-in
/// is a <c>user_audit</c> row. Nothing here logs an email, a password, a token or the cookie (spec 12).
/// </para>
/// <para>
/// M10 review: every security write that follows a decision (a revocation, a lockout count, an audit row) runs under
/// <see cref="CancellationToken.None"/>, so a caller who aborts the request cannot skip it.
/// </para>
/// </summary>
public static class AuthEndpoints
{
    public const string RefreshCookie = "qbautopost_refresh";

    public const string CookiePath = ApiRoutes.V1Prefix + "/auth";

    /// <summary>Spec 11.2: unknown email, wrong password and disabled user all get exactly this answer.</summary>
    public const string InvalidCredentialsType = "invalid-credentials";

    private static string? _timingHash;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder v1)
    {
        var auth = v1.MapGroup("/auth");
        // Answered from the caller CallerMiddleware resolved (which already passed the database gate).
        auth.MapGet("/me", GetMe);

        // Spec 10.7: everything that reads or writes users or tokens answers 503 while the database is not ready.
        var stored = auth.MapGroup(string.Empty).RequireDatabase();
        stored.MapPost("/login", LoginAsync);
        stored.MapPost("/refresh", RefreshAsync);
        stored.MapPost("/logout", LogoutAsync);
        stored.MapPost("/change-password", ChangePasswordAsync);
        return v1;
    }

    private static IResult GetMe(HttpContext http) => Results.Ok(AuthUserView.Of(CallerMiddleware.CallerOf(http)!));

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] LoginRequest? body,
        TokenService tokens,
        AuthBrake brake,
        IClock clock,
        IOptions<AppSettings> settings,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(AuthEndpoints));
        if (SignInOff(settings, log, http) is { } off)
        {
            return off;
        }

        var email = body?.Email?.Trim() ?? "";
        var password = body?.Password ?? "";
        if (email.Length == 0 || password.Length == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Email and password are required", "Send { email, password }.", "invalid-request");
        }

        var address = http.Connection.RemoteIpAddress;
        var emailKey = AuthBrake.EmailKey(email);
        var audit = http.RequestServices.GetRequiredService<UserAuditWriter>();
        var ip = address?.ToString();

        // Fix 1b: the failure is counted before the slow password check, atomically with the block test, so a
        // parallel burst cannot all pass the brake first. A sign-in that succeeds gives its reservation back.
        if (Reserve(brake, address, emailKey) is { } blocked)
        {
            await audit.WriteAsync(null, UserAuditWriter.Anonymous, UserAuditWriter.LoginFailed, new { email = Capped(email), address = ip, reason = "rate-limited" }, CancellationToken.None);
            log.LogWarning("Sign-in refused: too many failed sign-ins from this address or for this account");
            return TooMany(http, blocked);
        }

        var users = http.RequestServices.GetRequiredService<UserManager<AppUser>>();

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            // The same PBKDF2 work a real account costs, so the answer's timing does not say the email is unknown.
            users.PasswordHasher.VerifyHashedPassword(new AppUser(), TimingHash(users), password);
            // R10: no subject; the attempted email is kept in detail so that every failure is recorded (spec 11.2).
            await audit.WriteAsync(null, UserAuditWriter.Anonymous, UserAuditWriter.LoginFailed, new { email = Capped(email), address = ip, reason = "unknown-email" }, CancellationToken.None);
            log.LogWarning("Sign-in refused: no account holds the email given");
            return InvalidCredentials();
        }

        if (await users.IsLockedOutAsync(user))
        {
            await audit.WriteAsync(user.Id, UserAuditWriter.Anonymous, UserAuditWriter.LoginFailed, new { address = ip, reason = "locked-out" }, CancellationToken.None);
            log.LogWarning("Sign-in refused for user {UserId}: the account is locked out", user.Id);
            return LockedOut(http, user.LockoutEnd, clock);
        }

        var lockout = http.RequestServices.GetRequiredService<LockoutCounter>();
        if (!await users.CheckPasswordAsync(user, password))
        {
            // Fix 1a: counted atomically, so parallel wrong passwords each count.
            if (await lockout.RecordFailureAsync(user.Id, CancellationToken.None) is { } lockedUntil)
            {
                await audit.WriteAsync(user.Id, UserAuditWriter.Anonymous, UserAuditWriter.LockedOut, new { address = ip, reason = "failed-attempts" }, CancellationToken.None);
                log.LogWarning("User {UserId} locked out after repeated wrong passwords", user.Id);
                return LockedOut(http, lockedUntil, clock);
            }

            await audit.WriteAsync(user.Id, UserAuditWriter.Anonymous, UserAuditWriter.LoginFailed, new { address = ip, reason = "wrong-password" }, CancellationToken.None);
            log.LogWarning("Sign-in refused for user {UserId}: wrong password", user.Id);
            return InvalidCredentials();
        }

        if (user.IsDisabled)
        {
            // Spec 11.2: the same answer as wrong credentials, and counted the same (its reservation is kept), so it
            // says nothing more.
            await audit.WriteAsync(user.Id, UserAuditWriter.Anonymous, UserAuditWriter.LoginFailed, new { address = ip, reason = "disabled" }, CancellationToken.None);
            log.LogWarning("Sign-in refused for user {UserId}: the account is disabled", user.Id);
            return InvalidCredentials();
        }

        // Fix 3: what the answer reports must be stored. One retry on a fresh copy, then a 409.
        var now = clock.UtcNow;
        if (!await PersistAsync(http, users, user, u =>
            {
                u.AccessFailedCount = 0;
                u.LastLoginUtc = now;
            }))
        {
            log.LogWarning("Sign-in for user {UserId} not completed: the account changed while it was being updated", user.Id);
            return Problem(
                StatusCodes.Status409Conflict, "Sign-in not completed",
                "The account changed while signing in. Sign in again.", "concurrent-update");
        }

        // The address gets back only this attempt's own reservation: it is not forgiven earlier failures by one good
        // sign-in, or a guesser holding one real account could clear it between guesses at everyone else's.
        brake.Release(address);
        brake.RecordSuccess(emailKey);

        var refreshTokens = http.RequestServices.GetRequiredService<RefreshTokenService>();
        var refresh = await refreshTokens.IssueAsync(user.Id, familyId: null, ip, ct);
        SetRefreshCookie(http, refresh, refreshTokens.ExpiresFrom(clock.UtcNow));
        await audit.WriteAsync(user.Id, user.Id, UserAuditWriter.Login, new { address = ip }, CancellationToken.None);
        log.LogInformation("User {UserId} signed in", user.Id);
        return await SignedInAsync(http, users, user, tokens);
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext http, TokenService tokens, AuthBrake brake, IClock clock, IOptions<AppSettings> settings, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(AuthEndpoints));
        if (SignInOff(settings, log, http) is { } off)
        {
            return off;
        }

        var address = http.Connection.RemoteIpAddress;
        if (brake.IsBlocked(address, out var retryAfter))
        {
            log.LogWarning("Refresh refused: too many failed authentications from this address");
            return TooMany(http, retryAfter);
        }

        var presented = http.Request.Cookies[RefreshCookie];
        if (string.IsNullOrEmpty(presented))
        {
            log.LogWarning("Refresh refused: no refresh cookie");
            return InvalidRefresh(http);
        }

        var refreshTokens = http.RequestServices.GetRequiredService<RefreshTokenService>();
        var result = await refreshTokens.RotateAsync(presented, address?.ToString(), ct);
        switch (result.Outcome)
        {
            case RefreshOutcome.Invalid:
                brake.RecordFailure(address);
                log.LogWarning("Refresh refused: unknown or expired refresh token");
                return InvalidRefresh(http);
            case RefreshOutcome.ReuseDetected:
                log.LogWarning("Refresh refused: a revoked refresh token of user {UserId} was presented again; its whole family is revoked", result.UserId);
                return InvalidRefresh(http);
        }

        var users = http.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(result.UserId!);
        if (user is null || user.IsDisabled)
        {
            // Disabling revokes every token (spec 10.11); this is the belt to that braces.
            await refreshTokens.RevokeAllAsync(result.UserId!, CancellationToken.None);
            log.LogWarning("Refresh refused: user {UserId} is unknown or disabled", result.UserId);
            return InvalidRefresh(http);
        }

        SetRefreshCookie(http, result.NewToken!, refreshTokens.ExpiresFrom(clock.UtcNow));
        log.LogInformation("User {UserId} refreshed their access token", user.Id);
        return await SignedInAsync(http, users, user, tokens);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, ILoggerFactory loggers, CancellationToken ct)
    {
        var caller = CallerMiddleware.CallerOf(http)!;
        var presented = http.Request.Cookies[RefreshCookie];
        var revoked = false;
        if (!string.IsNullOrEmpty(presented))
        {
            // Fix 6: the token in the cookie, and nothing else of the caller's. Whoever holds a cookie can already use it,
            // so revoking it, even when it is somebody else's, only ever takes power away.
            revoked = await http.RequestServices.GetRequiredService<RefreshTokenService>().RevokeAsync(presented, CancellationToken.None);
        }

        ClearRefreshCookie(http);
        loggers.CreateLogger(typeof(AuthEndpoints))
            .LogInformation("User {UserId} signed out ({Revoked} refresh token revoked)", caller.Id, revoked ? 1 : 0);
        return Results.Ok(new SignedOutResponse(true));
    }

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext http,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ChangePasswordRequest? body,
        TokenService tokens,
        IClock clock,
        IOptions<AppSettings> settings,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var log = loggers.CreateLogger(typeof(AuthEndpoints));
        if (SignInOff(settings, log, http) is { } off)
        {
            return off;
        }

        var current = body?.CurrentPassword ?? "";
        var next = body?.NewPassword ?? "";
        if (current.Length == 0 || next.Length == 0)
        {
            return Problem(
                StatusCodes.Status400BadRequest, "Both passwords are required", "Send { currentPassword, newPassword }.", "invalid-request");
        }

        var caller = CallerMiddleware.CallerOf(http)!;
        var users = http.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(caller.Id);
        if (user is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, "Invalid or expired access token", "Sign in again.", "invalid-token");
        }

        if (await users.IsLockedOutAsync(user))
        {
            log.LogWarning("Password change refused for user {UserId}: the account is locked out", user.Id);
            return LockedOut(http, user.LockoutEnd, clock);
        }

        var audit = http.RequestServices.GetRequiredService<UserAuditWriter>();
        var ip = http.Connection.RemoteIpAddress?.ToString();

        // A stolen access token must not become a way to guess the password: a wrong one counts toward lockout,
        // atomically (fix 1a), and is recorded like any failed sign-in (fix 2).
        if (!await users.CheckPasswordAsync(user, current))
        {
            if (await http.RequestServices.GetRequiredService<LockoutCounter>().RecordFailureAsync(user.Id, CancellationToken.None) is { } lockedUntil)
            {
                await audit.WriteAsync(user.Id, user.Id, UserAuditWriter.LockedOut, new { address = ip, reason = "wrong-current-password" }, CancellationToken.None);
                log.LogWarning("User {UserId} locked out after repeated wrong current passwords", user.Id);
                return LockedOut(http, lockedUntil, clock);
            }

            await audit.WriteAsync(user.Id, user.Id, UserAuditWriter.LoginFailed, new { address = ip, reason = "wrong-current-password" }, CancellationToken.None);
            log.LogWarning("Password change refused for user {UserId}: the current password is wrong", user.Id);
            // SPEC-GAP T-1010 (Q-88): 11.2 names no answer for a wrong current password. 400, not 401, so the
            // dashboard does not take it for an expired session and sign the user out.
            return Problem(
                StatusCodes.Status400BadRequest, "Current password is wrong", "The current password does not match.", "current-password-incorrect");
        }

        var changed = await users.ChangePasswordAsync(user, current, next);
        if (!changed.Succeeded)
        {
            // Identity's descriptions name the rule (length), never the password.
            log.LogWarning("Password change refused for user {UserId}: the new password breaks the password rules", user.Id);
            return Problem(
                StatusCodes.Status400BadRequest, "New password not accepted",
                string.Join(" ", changed.Errors.Select(e => e.Description)), "invalid-password");
        }

        // ChangePasswordAsync has already rotated the security stamp, so every older access token stops working. The
        // password is changed, so the other sessions go and the change is recorded before anything else can fail.
        await http.RequestServices.GetRequiredService<RefreshTokenService>()
            .RevokeOthersAsync(user.Id, http.Request.Cookies[RefreshCookie], CancellationToken.None);
        await audit.WriteAsync(user.Id, user.Id, UserAuditWriter.PasswordChanged, new { address = ip }, CancellationToken.None);

        // Fix 3: one retry on a fresh copy, then a 409 rather than a mustChangePassword: false that is not stored.
        if (!await PersistAsync(http, users, user, u =>
            {
                u.AccessFailedCount = 0;
                u.MustChangePassword = false;
            }))
        {
            log.LogWarning("Password change for user {UserId}: the password changed, but the account could not be updated", user.Id);
            return Problem(
                StatusCodes.Status409Conflict, "Password changed, account not updated",
                "The password was changed, but the account changed at the same time. Sign in again with the new password.",
                "concurrent-update");
        }
        log.LogInformation("User {UserId} changed their password; their other refresh tokens are revoked", user.Id);
        return await SignedInAsync(http, users, user, tokens);
    }

    /// <summary>A fresh access token for <paramref name="user"/> and the answer every sign-in route gives.</summary>
    private static async Task<IResult> SignedInAsync(HttpContext http, UserManager<AppUser> users, AppUser user, TokenService tokens)
    {
        var role = RoleOf(await users.GetRolesAsync(user));
        var access = tokens.Issue(user, role, await users.GetSecurityStampAsync(user));
        var caller = Caller.ForUser(user.Id, TokenService.DisplayNameOf(user), user.Email, role, user.MustChangePassword);
        // FR-A-16 and spec 12: the audit trail names who signed in, as it names any other caller.
        http.Items[CallerMiddleware.CallerItemKey] ??= caller;
        return Results.Ok(new SignInResponse(access.Token, access.ExpiresUtc, AuthUserView.Of(caller)));
    }

    /// <summary>SPEC-GAP T-1009 (Q-87), as <see cref="UserGate"/>: no role, or more than one, is no role.</summary>
    private static string RoleOf(IList<string> roles) => roles.Count == 1 ? roles[0] : "";

    private static IResult? SignInOff(IOptions<AppSettings> settings, ILogger log, HttpContext http)
    {
        if (settings.Value.Auth.Jwt.HasUsableKey)
        {
            return null;
        }

        log.LogWarning(
            "Request {Method} {Path} refused: sign-in is off because Auth:Jwt:SigningKey is empty or shorter than {MinBytes} bytes",
            http.Request.Method, http.Request.Path.Value, JwtSettings.MinSigningKeyBytes);
        return Problem(
            StatusCodes.Status503ServiceUnavailable, "Sign-in not configured",
            "Dashboard sign-in is not configured on this server. API keys are unaffected.", "auth-not-configured");
    }

    /// <summary>Reserves a failure for both keys; null when both admitted it, else how long the caller must wait.</summary>
    private static TimeSpan? Reserve(AuthBrake brake, IPAddress? address, string emailKey)
    {
        var byAddress = brake.TryReserve(address, out var addressWait);
        var byEmail = brake.TryReserve(emailKey, out var emailWait);
        return byAddress && byEmail ? null : (addressWait > emailWait ? addressWait : emailWait);
    }

    private const int MaxAuditedEmail = 254;

    /// <summary>The attempted email as stored in <c>user_audit.detail</c>: at most <see cref="MaxAuditedEmail"/> characters.</summary>
    private static string Capped(string email) => email.Length <= MaxAuditedEmail ? email : email[..MaxAuditedEmail];

    /// <summary>
    /// Applies <paramref name="change"/> and saves; on a failure (a concurrency-stamp conflict, say) reloads the user
    /// from the database, applies it again and saves once more. False when neither save succeeded.
    /// </summary>
    internal static async Task<bool> PersistAsync(HttpContext http, UserManager<AppUser> users, AppUser user, Action<AppUser> change)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                // Identity's store works on the request's scoped context, so this is the copy UpdateAsync will save.
                await http.RequestServices.GetRequiredService<QbAutopostDbContext>().Entry(user).ReloadAsync(http.RequestAborted);
            }

            change(user);
            if ((await users.UpdateAsync(user)).Succeeded)
            {
                return true;
            }
        }

        return false;
    }

    private static IResult InvalidCredentials() =>
        Problem(StatusCodes.Status401Unauthorized, "Invalid credentials", "The email or the password is wrong.", InvalidCredentialsType);

    private static IResult InvalidRefresh(HttpContext http)
    {
        ClearRefreshCookie(http);
        return Problem(StatusCodes.Status401Unauthorized, "Sign in again", "The refresh token is missing, expired or revoked.", "invalid-refresh-token");
    }

    private static IResult TooMany(HttpContext http, TimeSpan retryAfter)
    {
        var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Problem(
            StatusCodes.Status429TooManyRequests, "Too many failed sign-ins",
            $"Too many failed sign-ins. Retry after {seconds} seconds.", "too-many-failed-sign-ins");
    }

    private static IResult LockedOut(HttpContext http, DateTimeOffset? end, IClock clock)
    {
        var seconds = end is { } until ? Math.Max(1, (int)Math.Ceiling((until.UtcDateTime - clock.UtcNow).TotalSeconds)) : 1;
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Problem(
            StatusCodes.Status423Locked, "Account locked",
            $"Too many wrong passwords. Try again in {seconds} seconds.", "locked-out");
    }

    private static void SetRefreshCookie(HttpContext http, string token, DateTime expiresUtc) =>
        http.Response.Cookies.Append(RefreshCookie, token, CookieOptions(expiresUtc));

    private static void ClearRefreshCookie(HttpContext http) => http.Response.Cookies.Delete(RefreshCookie, CookieOptions(null));

    private static CookieOptions CookieOptions(DateTime? expiresUtc) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expiresUtc is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : null,
        IsEssential = true,
    };

    /// <summary>A hash of no one's password, made once, to verify against when the email is unknown.</summary>
    private static string TimingHash(UserManager<AppUser> users) =>
        _timingHash ??= users.PasswordHasher.HashPassword(new AppUser(), Guid.NewGuid().ToString("N"));

    internal static IResult Problem(int status, string title, string detail, string type) =>
        Results.Problem(statusCode: status, title: title, detail: detail, type: type);
}
