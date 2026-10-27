using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

public sealed record CreateUserRequest(string? Email, string? DisplayName, string? Role);

public sealed record PatchUserRequest(string? DisplayName, string? Role, bool? Disabled);

/// <summary>Spec 11.4. The only concurrency token is the <c>ETag</c> header, Identity's <c>ConcurrencyStamp</c> (R9, R21).</summary>
public record UserView(
    string Id,
    string Email,
    string DisplayName,
    string Role,
    bool Disabled,
    bool MustChangePassword,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc,
    DateTime? LockedOutUntilUtc)
{
    public static UserView Of(AppUser user, string role, DateTime nowUtc) =>
        new(
            user.Id,
            user.Email ?? "",
            user.DisplayName,
            role,
            user.IsDisabled,
            user.MustChangePassword,
            user.CreatedUtc,
            user.LastLoginUtc,
            user.LockoutEnd is { } end && end.UtcDateTime > nowUtc ? end.UtcDateTime : null);
}

/// <summary>A <see cref="UserView"/> plus the temporary password, in the answer that creates or resets it and nowhere else.</summary>
public sealed record UserWithTemporaryPassword(
    string Id,
    string Email,
    string DisplayName,
    string Role,
    bool Disabled,
    bool MustChangePassword,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc,
    DateTime? LockedOutUntilUtc,
    string TemporaryPassword)
    : UserView(Id, Email, DisplayName, Role, Disabled, MustChangePassword, CreatedUtc, LastLoginUtc, LockedOutUntilUtc)
{
    public static UserWithTemporaryPassword Of(UserView view, string temporaryPassword) =>
        new(view.Id, view.Email, view.DisplayName, view.Role, view.Disabled, view.MustChangePassword, view.CreatedUtc,
            view.LastLoginUtc, view.LockedOutUntilUtc, temporaryPassword);
}

public sealed record UserPage(IReadOnlyList<UserView> Items, int Page, int PageSize, int Total);

public sealed record UserAuditView(long Id, string Kind, string Actor, string ActorName, JsonElement? Detail, DateTime AtUtc);

public sealed record UserAuditPage(IReadOnlyList<UserAuditView> Items, int Page, int PageSize, int Total);

/// <summary>
/// Saved-requests spec 11.4, 10.11, 10.12: user management for admins (<c>users:manage</c>, which only a signed-in
/// person holds). Served under <c>/api/v1</c> only, behind the database gate (spec 10.7).
/// <para>
/// R19: every path that changes a user's role, disables them or resets their password rotates their security stamp
/// (so older access tokens stop), drops the cached gate lookup (so that is at once, not after
/// <see cref="UserGate.CacheFor"/>) and revokes every refresh token. A role change replaces the old role in one save,
/// so a user never holds zero or two roles (Q-87).
/// </para>
/// <para>
/// R9: the ETag is Identity's <c>ConcurrencyStamp</c>. A failed sign-in rotates it too (<see cref="LockoutCounter"/>),
/// so an admin's ETag can go stale because of one; that answers <c>412</c> and the dashboard reloads.
/// </para>
/// <para>
/// 10.12: the last-admin rule holds under concurrency. A change to a user runs in a transaction that first takes row
/// locks on every admin (and the target) in id order, then reads the state, so two admins demoting each other at once
/// are decided one after the other and the second sees the first's result.
/// </para>
/// Nothing here logs a password; temporary passwords are returned once with <c>Cache-Control: no-store</c>.
/// </summary>
public static class UserEndpoints
{
    public const string LastAdminType = "last-admin";

    private const int DefaultPageSize = 25;

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder v1)
    {
        var users = v1.MapGroup("/users").RequireDatabase();
        users.MapGet("/", ListAsync);
        users.MapPost("/", CreateAsync);
        users.MapGet("/{id}", GetAsync);
        users.MapPatch("/{id}", PatchAsync);
        users.MapPost("/{id}/reset-password", ResetPasswordAsync);
        users.MapPost("/{id}/unlock", UnlockAsync);
        users.MapGet("/{id}/audit", AuditAsync);
        return v1;
    }

    // ---- list and read ----------------------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(HttpContext http, IOptions<AppSettings> settings, IClock clock, CancellationToken ct)
    {
        var query = http.Request.Query;
        string? role = null;
        if (query.TryGetValue("role", out var roleText) && (role = UserProvisioning.CanonicalRole(roleText.ToString())) is null)
        {
            return BadRequest("Unknown role", $"role must be one of {string.Join(", ", DashboardRoles.All)}.");
        }

        bool? disabled = null;
        if (query.TryGetValue("disabled", out var disabledText))
        {
            if (!bool.TryParse(disabledText.ToString(), out var parsed))
            {
                return BadRequest("Invalid disabled", "disabled must be true or false.");
            }

            disabled = parsed;
        }

        var max = Math.Max(1, settings.Value.SavedRequests.MaxListPageSize);
        if (Paging(query, max) is not { } paging)
        {
            return BadRequest("Invalid paging", $"page must be 1 or more and pageSize between 1 and {max}.");
        }

        var (page, pageSize) = paging;

        var db = http.RequestServices.GetRequiredService<QbAutopostDbContext>();
        var users = db.Users.AsNoTracking();
        if (role is not null)
        {
            var normalized = role.ToUpperInvariant();
            users = users.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == normalized)));
        }

        if (disabled is { } wanted)
        {
            users = users.Where(u => u.IsDisabled == wanted);
        }

        if (query.TryGetValue("q", out var q) && !string.IsNullOrWhiteSpace(q.ToString()))
        {
            // The text is a literal: % and _ are escaped, never read as wildcards.
            var pattern = "%" + q.ToString().Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            users = users.Where(u => EF.Functions.ILike(u.Email!, pattern, "\\") || EF.Functions.ILike(u.DisplayName, pattern, "\\"));
        }

        var total = await users.CountAsync(ct);
        var rows = await users.OrderBy(u => u.NormalizedEmail).ThenBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var roles = await RolesOfAsync(db, rows.Select(u => u.Id).ToList(), ct);
        var now = clock.UtcNow;
        var items = rows.Select(u => UserView.Of(u, RoleOf(roles, u.Id), now)).ToList();
        return Results.Ok(new UserPage(items, page, pageSize, total));
    }

    private static async Task<IResult> GetAsync(HttpContext http, string id, IClock clock, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<QbAutopostDbContext>();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        var roles = await RolesOfAsync(db, [id], ct);
        return WithEtag(http, user, Results.Ok(UserView.Of(user, RoleOf(roles, id), clock.UtcNow)));
    }

    // ---- create -----------------------------------------------------------------------------------------------------

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CreateUserRequest? body,
        IOptions<AppSettings> settings,
        IClock clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var email = body?.Email?.Trim() ?? "";
        var displayName = body?.DisplayName?.Trim() ?? "";
        var role = UserProvisioning.CanonicalRole(body?.Role);
        if (!UserProvisioning.IsEmail(email))
        {
            return BadRequest("Invalid email", "email must be an email address of at most 254 characters.");
        }

        if (displayName.Length == 0 || displayName.Length > UserProvisioning.MaxDisplayNameLength)
        {
            return BadRequest("Invalid displayName", $"displayName is required and at most {UserProvisioning.MaxDisplayNameLength} characters.");
        }

        if (role is null)
        {
            return BadRequest("Invalid role", $"role must be one of {string.Join(", ", DashboardRoles.All)}.");
        }

        var caller = CallerMiddleware.CallerOf(http)!;
        var services = http.RequestServices;
        var temporary = TemporaryPassword.Generate(settings.Value.Auth.Password.MinLength);
        var db = services.GetRequiredService<QbAutopostDbContext>();
        var now = clock.UtcNow;
        var result = await UserProvisioning.CreateAsync(
            services.GetRequiredService<UserManager<AppUser>>(), services.GetRequiredService<RoleManager<IdentityRole>>(),
            db, email, displayName, role, temporary, caller.Id, now, ct);
        switch (result.Outcome)
        {
            case ProvisionOutcome.EmailExists:
                return AuthEndpoints.Problem(
                    StatusCodes.Status409Conflict, "Email already in use", "A user with this email exists (email is not case-sensitive).", "email-exists");
            case ProvisionOutcome.Refused:
                // Identity's descriptions name a rule, never the password.
                return BadRequest("User not accepted", string.Join(" ", result.Errors));
        }

        var user = result.User!;
        await services.GetRequiredService<UserAuditWriter>()
            .WriteAsync(user.Id, caller.Id, UserAuditWriter.Created, new { role }, ct);
        loggers.CreateLogger(typeof(UserEndpoints)).LogInformation("User {UserId} created user {NewUserId} with role {Role}", caller.Id, user.Id, role);

        NoStore(http);
        var view = UserWithTemporaryPassword.Of(UserView.Of(user, role, now), temporary);
        http.Response.Headers.ETag = Etag(user);
        return Results.Created($"{ApiRoutes.V1Prefix}/users/{user.Id}", view);
    }

    // ---- patch ------------------------------------------------------------------------------------------------------

    private static async Task<IResult> PatchAsync(
        HttpContext http,
        string id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PatchUserRequest? body,
        IClock clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var ifMatch = IfMatchOf(http);
        if (ifMatch is null)
        {
            return AuthEndpoints.Problem(
                StatusCodes.Status428PreconditionRequired, "If-Match required",
                "Send the user's ETag in If-Match (GET /users/{id} returns it).", "precondition-required");
        }

        if (body is null || (body.DisplayName is null && body.Role is null && body.Disabled is null))
        {
            return BadRequest("Nothing to change", "Send at least one of displayName, role, disabled.");
        }

        string? newRole = null;
        if (body.Role is not null && (newRole = UserProvisioning.CanonicalRole(body.Role)) is null)
        {
            return BadRequest("Invalid role", $"role must be one of {string.Join(", ", DashboardRoles.All)}.");
        }

        var displayName = body.DisplayName?.Trim();
        if (displayName is not null && (displayName.Length == 0 || displayName.Length > UserProvisioning.MaxDisplayNameLength))
        {
            return BadRequest("Invalid displayName", $"displayName must be 1 to {UserProvisioning.MaxDisplayNameLength} characters.");
        }

        var services = http.RequestServices;
        var db = services.GetRequiredService<QbAutopostDbContext>();
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var caller = CallerMiddleware.CallerOf(http)!;
        if (!await db.Users.AnyAsync(u => u.Id == id, ct))
        {
            return NotFound();
        }

        if (body.Disabled == true && id == caller.Id)
        {
            return AuthEndpoints.Problem(
                StatusCodes.Status409Conflict, "You cannot disable yourself", "Ask another admin to disable this account.", "cannot-disable-self");
        }

        if (newRole is not null)
        {
            await UserProvisioning.EnsureRoleAsync(services.GetRequiredService<RoleManager<IdentityRole>>(), newRole);
        }

        AppUser user;
        string? previousRole;
        bool roleChanged;
        bool disabledChanged;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAdminsAndAsync(db, id, ct);

            // Read after the locks: a transaction that held them has committed, and this is a new statement.
            db.ChangeTracker.Clear();

            // The caller's row is in the lock set when they are an admin, so what is read here is final: an admin
            // demoted or disabled while this request was in flight (the gate may have cached them) is refused.
            var callerRoles = await RoleNamesAsync(db, caller.Id, ct);
            var callerRow = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == caller.Id, ct);
            if (callerRow is null || callerRow.IsDisabled || callerRoles.Count != 1 || callerRoles[0] != DashboardRoles.Admin)
            {
                return AuthEndpoints.Problem(
                    StatusCodes.Status403Forbidden, "Forbidden", "You are no longer an enabled admin.", "forbidden");
            }

            user = (await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct))!;
            if (user is null)
            {
                return NotFound();
            }

            if (!IfMatchHolds(ifMatch, user))
            {
                return AuthEndpoints.Problem(
                    StatusCodes.Status412PreconditionFailed, "Edited by someone else",
                    "The user changed since you loaded it. Reload and apply the change again.", "edited-by-someone-else");
            }

            var current = await RoleNamesAsync(db, id, ct);
            previousRole = current.Count == 1 ? current[0] : null;
            roleChanged = newRole is not null && !(current.Count == 1 && current[0] == newRole);
            disabledChanged = body.Disabled is { } d && d != user.IsDisabled;

            var wasAdmin = !user.IsDisabled && current.Count == 1 && current[0] == DashboardRoles.Admin;
            var willBeAdmin = !(body.Disabled ?? user.IsDisabled) && (roleChanged ? newRole : previousRole) == DashboardRoles.Admin;
            if (wasAdmin && !willBeAdmin && !await AnotherEnabledAdminExistsAsync(db, id, ct))
            {
                return AuthEndpoints.Problem(
                    StatusCodes.Status409Conflict, "This is the last admin",
                    "Make another user an enabled admin first.", LastAdminType);
            }

            if (displayName is not null)
            {
                user.DisplayName = displayName;
            }

            if (body.Disabled is { } disable)
            {
                user.IsDisabled = disable;
            }

            if (roleChanged)
            {
                await ReplaceRoleAsync(db, id, newRole!, ct);
            }

            // One save carries the user and the role rows; the stamp is rotated inside the same transaction.
            var saved = await users.UpdateAsync(user);
            var stamped = saved.Succeeded && (!(roleChanged || body.Disabled == true) || (await users.UpdateSecurityStampAsync(user)).Succeeded);
            if (!stamped)
            {
                await transaction.RollbackAsync(ct);
                return AuthEndpoints.Problem(
                    StatusCodes.Status409Conflict, "Not saved", "The user changed while it was being updated. Reload and try again.", "concurrent-update");
            }

            await transaction.CommitAsync(ct);
        }

        var sensitive = roleChanged || body.Disabled == true;
        if (sensitive)
        {
            await EndSessionsAsync(services, user.Id, ct);
        }

        var audit = services.GetRequiredService<UserAuditWriter>();
        if (roleChanged)
        {
            await audit.WriteAsync(user.Id, caller.Id, UserAuditWriter.RoleChanged, new { from = previousRole, to = newRole }, ct);
        }

        if (disabledChanged)
        {
            await audit.WriteAsync(user.Id, caller.Id, body.Disabled == true ? UserAuditWriter.Disabled : UserAuditWriter.Enabled, new { }, ct);
        }

        loggers.CreateLogger(typeof(UserEndpoints)).LogInformation(
            "User {UserId} changed user {TargetUserId}: roleChanged={RoleChanged} disabledChanged={DisabledChanged}",
            caller.Id, user.Id, roleChanged, disabledChanged);
        var role = roleChanged ? newRole! : previousRole ?? "";
        return WithEtag(http, user, Results.Ok(UserView.Of(user, role, clock.UtcNow)));
    }

    // ---- reset password and unlock ---------------------------------------------------------------------------------

    private static async Task<IResult> ResetPasswordAsync(
        HttpContext http, string id, IOptions<AppSettings> settings, IClock clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var services = http.RequestServices;
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var caller = CallerMiddleware.CallerOf(http)!;
        var temporary = TemporaryPassword.Generate(settings.Value.Auth.Password.MinLength);
        // R19: the new hash, mustChangePassword and the rotated security stamp are one write, so a lost race leaves
        // either nothing changed (409) or all of it. Whenever the hash was saved the sessions end, below.
        var reset = await AuthEndpoints.PersistAsync(http, users, user, u =>
        {
            u.PasswordHash = users.PasswordHasher.HashPassword(u, temporary);
            u.MustChangePassword = true;
            u.SecurityStamp = Guid.NewGuid().ToString();
        });
        if (!reset)
        {
            return AuthEndpoints.Problem(
                StatusCodes.Status409Conflict, "Password not reset", "The user changed while it was being updated. Nothing was changed; try again.", "concurrent-update");
        }

        await EndSessionsAsync(services, user.Id, ct);
        await services.GetRequiredService<UserAuditWriter>().WriteAsync(user.Id, caller.Id, UserAuditWriter.PasswordReset, new { }, ct);
        loggers.CreateLogger(typeof(UserEndpoints)).LogInformation("User {UserId} reset the password of user {TargetUserId}", caller.Id, user.Id);

        NoStore(http);
        http.Response.Headers.ETag = Etag(user);
        var db = services.GetRequiredService<QbAutopostDbContext>();
        var role = RoleOf(await RolesOfAsync(db, [user.Id], ct), user.Id);
        return Results.Ok(UserWithTemporaryPassword.Of(UserView.Of(user, role, clock.UtcNow), temporary));
    }

    private static async Task<IResult> UnlockAsync(HttpContext http, string id, IClock clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var services = http.RequestServices;
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var caller = CallerMiddleware.CallerOf(http)!;
        if (!await AuthEndpoints.PersistAsync(http, users, user, u =>
            {
                u.LockoutEnd = null;
                u.AccessFailedCount = 0;
            }))
        {
            return AuthEndpoints.Problem(
                StatusCodes.Status409Conflict, "Not unlocked", "The user changed while it was being updated. Try again.", "concurrent-update");
        }

        await services.GetRequiredService<UserAuditWriter>().WriteAsync(user.Id, caller.Id, UserAuditWriter.Unlocked, new { }, ct);
        loggers.CreateLogger(typeof(UserEndpoints)).LogInformation("User {UserId} unlocked user {TargetUserId}", caller.Id, user.Id);
        var db = services.GetRequiredService<QbAutopostDbContext>();
        var role = RoleOf(await RolesOfAsync(db, [user.Id], ct), user.Id);
        return WithEtag(http, user, Results.Ok(UserView.Of(user, role, clock.UtcNow)));
    }

    // ---- audit ------------------------------------------------------------------------------------------------------

    private static async Task<IResult> AuditAsync(HttpContext http, string id, IOptions<AppSettings> settings, CancellationToken ct)
    {
        var max = Math.Max(1, settings.Value.SavedRequests.MaxListPageSize);
        if (Paging(http.Request.Query, max) is not { } paging)
        {
            return BadRequest("Invalid paging", $"page must be 1 or more and pageSize between 1 and {max}.");
        }

        var (page, pageSize) = paging;

        var db = http.RequestServices.GetRequiredService<QbAutopostDbContext>();
        if (!await db.Users.AnyAsync(u => u.Id == id, ct))
        {
            return NotFound();
        }

        var rows = db.UserAudit.AsNoTracking().Where(a => a.UserId == id);
        var total = await rows.CountAsync(ct);
        var entries = await rows.OrderByDescending(a => a.AtUtc).ThenByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // Spec 5.3: the actor's name beside their id. Anything that is not a user id (anonymous, api-client:x) stays as is.
        var actorIds = entries.Select(e => e.Actor).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var items = entries
            .Select(e => new UserAuditView(e.Id, e.Kind, e.Actor, names.GetValueOrDefault(e.Actor) ?? e.Actor, DetailOf(e.Detail), e.AtUtc))
            .ToList();
        return Results.Ok(new UserAuditPage(items, page, pageSize, total));
    }

    // ---- shared steps -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes row locks, in id order, on the target and on every user holding the admin role. Two changes that touch
    /// the admins queue here, and the second reads what the first committed.
    /// </summary>
    private static Task<int> LockAdminsAndAsync(QbAutopostDbContext db, string targetId, CancellationToken ct)
    {
        var admin = DashboardRoles.Admin.ToUpperInvariant();
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT u.id FROM users u
            WHERE u.id = {targetId}
               OR u.id IN (SELECT ur.user_id FROM user_roles ur JOIN roles r ON r.id = ur.role_id WHERE r.normalized_name = {admin})
            ORDER BY u.id
            FOR UPDATE
            """,
            ct);
    }

    /// <summary>An enabled user other than <paramref name="exceptId"/> who holds exactly the admin role (a user with two roles has no scopes, Q-87).</summary>
    private static Task<bool> AnotherEnabledAdminExistsAsync(QbAutopostDbContext db, string exceptId, CancellationToken ct)
    {
        var admin = DashboardRoles.Admin.ToUpperInvariant();
        return db.Users.AsNoTracking().AnyAsync(
            u => u.Id != exceptId
                 && !u.IsDisabled
                 && db.UserRoles.Count(ur => ur.UserId == u.Id) == 1
                 && db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == admin)),
            ct);
    }

    /// <summary>Removes every role of the user but <paramref name="role"/>, and adds it if missing. Saved by the caller.</summary>
    private static async Task ReplaceRoleAsync(QbAutopostDbContext db, string userId, string role, CancellationToken ct)
    {
        var normalized = role.ToUpperInvariant();
        var roleId = await db.Roles.Where(r => r.NormalizedName == normalized).Select(r => r.Id).SingleAsync(ct);
        var existing = await db.UserRoles.Where(ur => ur.UserId == userId).ToListAsync(ct);
        db.UserRoles.RemoveRange(existing.Where(ur => ur.RoleId != roleId));
        if (existing.All(ur => ur.RoleId != roleId))
        {
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = roleId });
        }
    }

    /// <summary>R19: the stamp is rotated, the cached lookup dropped and every refresh token revoked.</summary>
    private static async Task EndSessionsAsync(IServiceProvider services, string userId, CancellationToken ct)
    {
        UserGate.Forget(services.GetRequiredService<IMemoryCache>(), userId);
        await services.GetRequiredService<RefreshTokenService>().RevokeAllAsync(userId, ct);
    }

    private static async Task<List<string>> RoleNamesAsync(QbAutopostDbContext db, string userId, CancellationToken ct) =>
        await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == userId select r.Name!).ToListAsync(ct);

    private static async Task<Dictionary<string, List<string>>> RolesOfAsync(QbAutopostDbContext db, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var pairs = await (from ur in db.UserRoles.AsNoTracking()
                           join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                           where userIds.Contains(ur.UserId)
                           select new { ur.UserId, Role = r.Name! }).ToListAsync(ct);
        return pairs.GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.Select(p => p.Role).ToList());
    }

    /// <summary>SPEC-GAP T-1009 (Q-87), as <see cref="UserGate"/>: no role, or more than one, is no role.</summary>
    private static string RoleOf(Dictionary<string, List<string>> roles, string userId) =>
        roles.TryGetValue(userId, out var names) && names.Count == 1 ? names[0] : "";

    private static JsonElement? DetailOf(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detail);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>(page, pageSize), or null when either is not a whole number in range.</summary>
    private static (int Page, int PageSize)? Paging(IQueryCollection query, int maxPageSize)
    {
        var page = 1;
        var pageSize = Math.Min(DefaultPageSize, maxPageSize);
        if (query.TryGetValue("page", out var pageText)
            && (!int.TryParse(pageText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
        {
            return null;
        }

        if (query.TryGetValue("pageSize", out var sizeText)
            && (!int.TryParse(sizeText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize < 1 || pageSize > maxPageSize))
        {
            return null;
        }

        return (page, pageSize);
    }

    /// <summary>The If-Match value without quotes or the weak marker; null when the header is absent.</summary>
    private static string? IfMatchOf(HttpContext http)
    {
        var header = http.Request.Headers.IfMatch.ToString().Trim();
        if (header.Length == 0)
        {
            return null;
        }

        if (header.StartsWith("W/", StringComparison.Ordinal))
        {
            header = header[2..];
        }

        return header.Trim('"');
    }

    private static bool IfMatchHolds(string ifMatch, AppUser user) =>
        user.ConcurrencyStamp is { Length: > 0 } stamp && string.Equals(ifMatch, stamp, StringComparison.Ordinal);

    private static string Etag(AppUser user) => $"\"{user.ConcurrencyStamp}\"";

    private static IResult WithEtag(HttpContext http, AppUser user, IResult result)
    {
        http.Response.Headers.ETag = Etag(user);
        return result;
    }

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    private static IResult NotFound() =>
        AuthEndpoints.Problem(StatusCodes.Status404NotFound, "User not found", "No user has this id.", "not-found");

    private static IResult BadRequest(string title, string detail) =>
        AuthEndpoints.Problem(StatusCodes.Status400BadRequest, title, detail, "invalid-request");
}
