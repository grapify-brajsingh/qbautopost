using Microsoft.EntityFrameworkCore;
using QbAutopost.Api.Security;
using QbAutopost.Core.Security;
using QbAutopost.Data;

namespace QbAutopost.Api.SavedRequests;

/// <summary>A dashboard user's name and email, as history shows them beside the id (spec 5.3).</summary>
public sealed record UserName(string Name, string? Email);

/// <summary>Looks up dashboard users by id, many at once, so a page of history costs one query rather than one per row.</summary>
public interface IUserDirectory
{
    Task<IReadOnlyDictionary<string, UserName>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct);
}

/// <summary>The PostgreSQL user table. Users are disabled, never deleted (spec 5.3), so an id in history always resolves.</summary>
public sealed class DbUserDirectory(IDbContextFactory<QbAutopostDbContext> factory) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<string, UserName>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<string, UserName>();
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserName(u.DisplayName, u.Email), StringComparer.Ordinal, ct);
    }
}

/// <summary>Who an actor is, as the views show it: <c>{ id, name, email }</c>; email only for a user.</summary>
public sealed record ActorView(string Id, string Name, string? Email);

/// <summary>The names resolved for one response. An actor nobody can name is shown by its id.</summary>
public sealed class ActorNames(IReadOnlyDictionary<string, UserName> users, IReadOnlyDictionary<string, string> clients)
{
    public const string ClientPrefix = "api-client:";

    public ActorView Of(string actor)
    {
        if (actor.StartsWith(ClientPrefix, StringComparison.Ordinal))
        {
            var clientId = actor[ClientPrefix.Length..];
            return new ActorView(actor, clients.GetValueOrDefault(clientId) ?? clientId, null);
        }

        return users.TryGetValue(actor, out var user)
            ? new ActorView(actor, user.Name, user.Email)
            : new ActorView(actor, actor, null);
    }

    public string ClientName(string clientId) => clients.GetValueOrDefault(clientId) ?? clientId;

    /// <summary>A dashboard user the lookup found, or null (an API client, or an id nobody knows).</summary>
    public UserName? UserOf(string id) => users.GetValueOrDefault(id);
}

/// <summary>
/// Spec 5.3: names for the actors in a response, users from the user table (one batched lookup) and API clients from
/// <c>clients.json</c>. Without a database there is no user table, and the ids are shown as they are.
/// </summary>
public sealed class ActorDirectory(ApiClientStore clientStore, IServiceProvider services)
{
    /// <param name="actors">History actors (a user id or <c>api-client:&lt;id&gt;</c>); duplicates are fine.</param>
    public async Task<ActorNames> ResolveAsync(IEnumerable<string?> actors, CancellationToken ct)
    {
        var userIds = actors
            .Where(a => !string.IsNullOrEmpty(a) && !a.StartsWith(ActorNames.ClientPrefix, StringComparison.Ordinal))
            .Select(a => a!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var directory = services.GetService<IUserDirectory>();
        var users = directory is null || userIds.Count == 0
            ? new Dictionary<string, UserName>()
            : await directory.FindAsync(userIds, ct);
        return new ActorNames(users, ClientNames());
    }

    private Dictionary<string, string> ClientNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CallerMiddleware.LegacyClientId] = "the shared Api:ApiKey",
        };
        foreach (var client in clientStore.Load().Clients)
        {
            names[client.Id] = string.IsNullOrWhiteSpace(client.Name) ? client.Id : client.Name;
        }

        return names;
    }
}
