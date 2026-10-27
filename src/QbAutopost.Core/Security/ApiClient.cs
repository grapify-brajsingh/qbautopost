using System.Net;

namespace QbAutopost.Core.Security;

/// <summary>
/// The scopes a caller key may carry (FR-A-13). They name what a caller may <i>do</i>, not which route they call, so
/// a new route inherits an existing answer instead of inventing a new permission.
/// </summary>
public static class ApiScopes
{
    public const string HealthRead = "health:read";
    public const string QbRead = "qb:read";
    public const string QbPost = "qb:post";

    /// <summary>Lets a post use Hermes for account choice (Q-51); needed on top of <see cref="QbPost"/>.</summary>
    public const string QbPostAi = "qb:post:ai";

    /// <summary>Lets a response carry raw qbXML, which holds every amount and name in the batch (FR-A-6).</summary>
    public const string QbDebug = "qb:debug";

    public const string JobsRead = "jobs:read";
    public const string JobsWrite = "jobs:write";
    public const string RulesWrite = "rules:write";

    /// <summary>
    /// Everything an API key can do, held by the implicit legacy client and by whoever issues keys. Since T-1009 it
    /// means "every scope except <see cref="PeopleOnly"/>" (saved-requests spec 11.3).
    /// </summary>
    public const string Admin = "admin";

    // Saved-requests spec 11.3. Who holds each is decided by DashboardRoles for people and clients.json for keys.
    public const string RequestsSubmit = "requests:submit";
    public const string RequestsRead = "requests:read";
    public const string RequestsEdit = "requests:edit";
    public const string RequestsPost = "requests:post";
    public const string RequestsAdmin = "requests:admin";
    public const string UsersManage = "users:manage";

    /// <summary><c>GET /quickbooks/lists</c> only. A key holding <see cref="QbRead"/> satisfies it as well.</summary>
    public const string ListsRead = "lists:read";

    /// <summary>
    /// Not a grantable scope: what a route needs when any signed-in person may call it and no API key may
    /// (<c>/auth/me</c>, <c>/auth/logout</c>, <c>/auth/change-password</c>, spec 11.2). No key ever satisfies it.
    /// </summary>
    public const string SignedIn = "signed-in";

    /// <summary>Joins alternatives in a required scope: <c>a|b</c> is satisfied by either.</summary>
    public const char AlternativeSeparator = '|';

    /// <summary>
    /// The routes behind these four answer only to a signed-in person (spec 11.3, "a named person pressed Post"). An
    /// API key never satisfies them, whatever it holds, <see cref="Admin"/> and the legacy shared key included.
    /// </summary>
    public static IReadOnlySet<string> PeopleOnly { get; } =
        new HashSet<string>([RequestsEdit, RequestsPost, RequestsAdmin, UsersManage], StringComparer.OrdinalIgnoreCase);

    /// <summary>The scopes that may be written into <c>clients.json</c>.</summary>
    public static IReadOnlyList<string> All { get; } =
        [HealthRead, QbRead, QbPost, QbPostAi, QbDebug, JobsRead, JobsWrite, RulesWrite, Admin, RequestsSubmit, RequestsRead, ListsRead];

    /// <summary>A required scope naming several alternatives (<see cref="AnyOf"/>) as its parts; otherwise itself.</summary>
    public static string AnyOf(params string[] scopes) => string.Join(AlternativeSeparator, scopes);

    /// <summary>The alternatives in <paramref name="required"/>; a plain scope is its own single alternative.</summary>
    public static IEnumerable<string> Alternatives(string required) =>
        required.Split(AlternativeSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when an API key holding <paramref name="held"/> satisfies <paramref name="required"/>.</summary>
    public static bool KeyAllows(IEnumerable<string> held, string required)
    {
        var scopes = held as IReadOnlyCollection<string> ?? held.ToArray();
        return Alternatives(required).Any(scope => KeyAllowsOne(scopes, scope));
    }

    private static bool KeyAllowsOne(IReadOnlyCollection<string> held, string scope)
    {
        if (PeopleOnly.Contains(scope) || scope.Equals(SignedIn, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return held.Contains(scope, StringComparer.OrdinalIgnoreCase)
               || held.Contains(Admin, StringComparer.OrdinalIgnoreCase)
               || (scope.Equals(ListsRead, StringComparison.OrdinalIgnoreCase)
                   && held.Contains(QbRead, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// One caller (FR-A-13). The key itself is never here: only a salted hash, so this file leaking is not a breach.
/// </summary>
public sealed record ApiClient
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    /// <summary>Base64 SHA-256 over <c>salt || key</c>.</summary>
    public required string KeyHash { get; init; }

    /// <summary>Base64 salt, unique per client, so one stolen hash does not identify every caller sharing a key.</summary>
    public required string KeySalt { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public bool Enabled { get; init; } = true;

    public DateTime CreatedUtc { get; init; }

    public DateTime? ExpiresUtc { get; init; }

    /// <summary>The key being rotated out; accepted until <see cref="PreviousExpiresUtc"/> so a caller has no downtime.</summary>
    public string? PreviousKeyHash { get; init; }

    public string? PreviousKeySalt { get; init; }

    public DateTime? PreviousExpiresUtc { get; init; }

    /// <summary>When set, the caller is only admitted from these networks. Empty means "from anywhere".</summary>
    public IReadOnlyList<string> AllowedCidrs { get; init; } = [];

    /// <summary>FR-A-15: this client's own rate limits, when the defaults do not suit them.</summary>
    public int? PostPerMinute { get; init; }

    public int? DefaultPerMinute { get; init; }

    public bool IsUsable(DateTime utcNow) => Enabled && (ExpiresUtc is null || ExpiresUtc > utcNow);

    /// <summary>
    /// <c>admin</c> carries every scope but <see cref="ApiScopes.PeopleOnly"/>; otherwise the scope must be listed.
    /// <see cref="ApiScopes.QbRead"/> also satisfies <see cref="ApiScopes.ListsRead"/>.
    /// </summary>
    public bool Allows(string scope) => ApiScopes.KeyAllows(Scopes, scope);

    /// <summary>
    /// True when the caller may be admitted from <paramref name="address"/>. With no CIDR listed, anywhere; with one
    /// listed, an address that cannot be shown to be inside it is outside it — including a request with no address
    /// at all. A CIDR that does not parse is ignored rather than treated as "everything".
    /// </summary>
    public bool AllowsAddress(IPAddress? address)
    {
        if (AllowedCidrs.Count == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        foreach (var cidr in AllowedCidrs)
        {
            if (IPNetwork.TryParse(cidr, out var network) && network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary><c>clients.json</c> (FR-A-13), git-ignored and holding no key in the clear.</summary>
public sealed record ApiClientList
{
    public IReadOnlyList<ApiClient> Clients { get; init; } = [];
}
