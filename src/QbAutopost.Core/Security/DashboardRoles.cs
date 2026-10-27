namespace QbAutopost.Core.Security;

/// <summary>
/// The three dashboard roles and the scopes each receives (saved-requests spec 11.1, 11.3). Roles receive only these:
/// never <c>qb:read</c>, <c>health:read</c>, <c>qb:post</c>, <c>jobs:*</c>, <c>rules:write</c> or <c>admin</c>.
/// </summary>
public static class DashboardRoles
{
    public const string Admin = "admin";
    public const string Editor = "editor";
    public const string Viewer = "viewer";

    private static readonly IReadOnlySet<string> None = Set();

    private static readonly IReadOnlySet<string> ViewerScopes = Set(ApiScopes.RequestsRead, ApiScopes.ListsRead);

    private static readonly IReadOnlySet<string> EditorScopes =
        Set([.. ViewerScopes, ApiScopes.RequestsSubmit, ApiScopes.RequestsEdit, ApiScopes.RequestsPost]);

    private static readonly IReadOnlySet<string> AdminScopes =
        Set([.. EditorScopes, ApiScopes.RequestsAdmin, ApiScopes.UsersManage]);

    public static IReadOnlyList<string> All { get; } = [Admin, Editor, Viewer];

    /// <summary>The scopes of <paramref name="role"/>; none for a role this build does not know (hold, don't guess).</summary>
    public static IReadOnlySet<string> ScopesOf(string? role) => role?.ToLowerInvariant() switch
    {
        Viewer => ViewerScopes,
        Editor => EditorScopes,
        Admin => AdminScopes,
        _ => None,
    };

    private static HashSet<string> Set(params string[] scopes) => new(scopes, StringComparer.OrdinalIgnoreCase);
}
