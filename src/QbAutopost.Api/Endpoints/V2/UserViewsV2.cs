using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Endpoints.V2;

/// <summary>
/// T-1115 (dashboard v2 spec 12): a user's status. Exactly one holds, evaluated in this order: <see cref="Disabled"/>,
/// <see cref="Locked"/> (not disabled and <c>lockout_end</c> is after now), <see cref="MustChangePassword"/>,
/// <see cref="Active"/>. Documented as the <c>UserStatus</c> enum in <c>openapi-v2.json</c>.
/// </summary>
public static class UserStatuses
{
    public const string Active = "active";
    public const string MustChangePassword = "must-change-password";
    public const string Locked = "locked";
    public const string Disabled = "disabled";

    public static readonly IReadOnlyList<string> All = [Active, MustChangePassword, Locked, Disabled];

    /// <summary>True when <paramref name="value"/> is one of <see cref="All"/>, exactly as written.</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    public static string Of(AppUser user, DateTime nowUtc) =>
        user.IsDisabled ? Disabled
        : user.LockoutEnd is { } end && end.UtcDateTime > nowUtc ? Locked
        : user.MustChangePassword ? MustChangePassword
        : Active;
}

/// <summary>A v2 <c>GET /users</c> row: the v1 <see cref="UserView"/> fields plus <see cref="Status"/> (spec 12).</summary>
public sealed record UserViewV2(
    string Id,
    string Email,
    string DisplayName,
    string Role,
    bool Disabled,
    bool MustChangePassword,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc,
    DateTime? LockedOutUntilUtc,
    string Status)
{
    public static UserViewV2 Of(AppUser user, string role, DateTime nowUtc)
    {
        var view = UserView.Of(user, role, nowUtc);
        return new(view.Id, view.Email, view.DisplayName, view.Role, view.Disabled, view.MustChangePassword, view.CreatedUtc,
            view.LastLoginUtc, view.LockedOutUntilUtc, UserStatuses.Of(user, nowUtc));
    }
}

public sealed record UserPageV2(IReadOnlyList<UserViewV2> Items, int Page, int PageSize, int Total);
