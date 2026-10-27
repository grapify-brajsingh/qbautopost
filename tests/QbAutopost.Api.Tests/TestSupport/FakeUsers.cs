using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using QbAutopost.Api.Auth;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// Rule 1: no PostgreSQL, so no <c>UserManager</c>. Replaces <see cref="IUserGate"/> with an in-memory user list and
/// issues access tokens signed with <see cref="ApiFactory.SigningKey"/>, shaped as spec 11.2 says T-1010 will issue
/// them. The gate's 30 s cache is tested on the real <see cref="UserGate"/>, not here.
/// </summary>
public sealed class FakeUsers : IUserGate
{
    private readonly ConcurrentDictionary<string, User> _users = new();
    private int _next;
    private int _resolutions;

    /// <summary>Adds a user with <paramref name="role"/> and returns a valid access token for them.</summary>
    public string TokenFor(string role, bool mustChange = false, bool disabled = false) =>
        TokenOf(Add(role, mustChange, disabled));

    /// <summary>A token whose lifetime ended five minutes ago, for an otherwise good user.</summary>
    public string ExpiredTokenFor(string role)
    {
        var user = Add(role, false, false);
        var now = DateTime.UtcNow;
        return Sign(user, ApiFactory.SigningKey, now.AddMinutes(-20), now.AddMinutes(-5));
    }

    /// <summary>A well-formed token for a good user, signed with a key the server does not hold.</summary>
    public string ForgedTokenFor(string role) =>
        Sign(Add(role, false, false), "a-different-key-that-is-long-enough-0123456789", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15));

    /// <summary>How many times a token reached the user lookup; the database gate must stop it first when not ready.</summary>
    public int Resolutions => _resolutions;

    public string IdOf(string token) => new JsonWebToken(token).Subject;

    public Task<Caller?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        Interlocked.Increment(ref _resolutions);
        var id = principal.FindFirstValue(AccessTokens.Subject);
        var stamp = principal.FindFirstValue(AccessTokens.Stamp);
        var caller = id is not null && _users.TryGetValue(id, out var user) && !user.Disabled && user.Stamp == stamp
            ? Caller.ForUser(user.Id, user.Name, user.Email, user.Role, user.MustChange)
            : null;
        return Task.FromResult(caller);
    }

    private User Add(string role, bool mustChange, bool disabled)
    {
        var n = Interlocked.Increment(ref _next);
        var user = new User($"user-{n}", $"User {n}", $"user{n}@example.com", role, Guid.NewGuid().ToString("N"), disabled, mustChange);
        _users[user.Id] = user;
        return user;
    }

    private static string TokenOf(User user) =>
        Sign(user, ApiFactory.SigningKey, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15));

    private static string Sign(User user, string key, DateTime notBefore, DateTime expires) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "qbautopost",
            Audience = "qbautopost-dashboard",
            NotBefore = notBefore,
            IssuedAt = notBefore,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                [AccessTokens.Subject] = user.Id,
                [AccessTokens.Email] = user.Email,
                [AccessTokens.Name] = user.Name,
                [AccessTokens.Role] = user.Role,
                [AccessTokens.Stamp] = user.Stamp,
                [AccessTokens.TokenId] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256),
        });

    private sealed record User(
        string Id, string Name, string Email, string Role, string Stamp, bool Disabled, bool MustChange);
}
