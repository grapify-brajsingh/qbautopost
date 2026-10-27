namespace QbAutopost.Core.Security;

/// <summary>Who is calling (saved-requests spec 11.3): a signed-in dashboard user, or an API key from <c>clients.json</c>.</summary>
public enum CallerKind
{
    User,
    ApiClient,
}

/// <summary>
/// The caller of one request, whichever credential it brought. A user's scopes are their role's
/// (<see cref="DashboardRoles"/>); a key's are what <c>clients.json</c> lists, read with the key rules of
/// <see cref="ApiScopes.KeyAllows"/>.
/// </summary>
public sealed record Caller(
    CallerKind Kind,
    string Id,
    string Name,
    string? Email,
    string? Role,
    IReadOnlySet<string> Scopes,
    bool MustChangePassword)
{
    /// <summary>What history columns record (spec 5.3): the user id, or <c>api-client:&lt;id&gt;</c>.</summary>
    public string Actor => Kind == CallerKind.User ? Id : $"api-client:{Id}";

    /// <summary>True when this caller satisfies <paramref name="scope"/>, or any of its alternatives.</summary>
    public bool Allows(string scope) =>
        Kind == CallerKind.ApiClient
            ? ApiScopes.KeyAllows(Scopes, scope)
            : ApiScopes.Alternatives(scope).Any(s =>
                s.Equals(ApiScopes.SignedIn, StringComparison.OrdinalIgnoreCase) || Scopes.Contains(s));

    /// <summary>The caller an API key resolves to.</summary>
    public static Caller ForClient(ApiClient client) =>
        new(
            CallerKind.ApiClient,
            client.Id,
            client.Name ?? client.Id,
            null,
            null,
            new HashSet<string>(client.Scopes, StringComparer.OrdinalIgnoreCase),
            false);

    /// <summary>The caller a signed-in user resolves to, with the scopes of <paramref name="role"/> and no others.</summary>
    public static Caller ForUser(string id, string name, string? email, string role, bool mustChangePassword) =>
        new(CallerKind.User, id, name, email, role, DashboardRoles.ScopesOf(role), mustChangePassword);
}
