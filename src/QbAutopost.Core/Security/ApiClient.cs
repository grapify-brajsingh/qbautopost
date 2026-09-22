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

    /// <summary>Everything. Held by the implicit legacy client, and by whoever issues keys.</summary>
    public const string Admin = "admin";

    public static IReadOnlyList<string> All { get; } =
        [HealthRead, QbRead, QbPost, QbPostAi, QbDebug, JobsRead, JobsWrite, RulesWrite, Admin];
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

    /// <summary><c>admin</c> carries every scope; otherwise the scope must be listed.</summary>
    public bool Allows(string scope) =>
        Scopes.Contains(ApiScopes.Admin, StringComparer.OrdinalIgnoreCase)
        || Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);

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
