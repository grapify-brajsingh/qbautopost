using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Auth;

/// <summary>An issued access token and the moment it stops working.</summary>
public sealed record AccessToken(string Token, DateTime ExpiresUtc);

/// <summary>
/// Saved-requests spec 11.2: issues the dashboard access token, HS256 over <c>Auth:Jwt:SigningKey</c>, valid for
/// <c>AccessTokenMinutes</c>, typed <see cref="AccessTokens.TokenType"/> (R17), with exactly the claims
/// <c>CallerMiddleware</c> reads back. Callers check <see cref="JwtSettings.HasUsableKey"/> first.
/// </summary>
public sealed class TokenService(IOptions<AppSettings> settings, IClock clock)
{
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public AccessToken Issue(AppUser user, string role, string securityStamp)
    {
        var jwt = settings.Value.Auth.Jwt;
        if (!jwt.HasUsableKey)
        {
            throw new InvalidOperationException("Auth:Jwt:SigningKey is empty or too short; no access token can be issued.");
        }

        var now = clock.UtcNow;
        var expires = now.AddMinutes(jwt.AccessTokenMinutes);
        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = AccessTokens.TokenType,
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                [AccessTokens.Subject] = user.Id,
                [AccessTokens.Email] = user.Email ?? "",
                [AccessTokens.Name] = DisplayNameOf(user),
                [AccessTokens.Role] = role,
                [AccessTokens.Stamp] = securityStamp,
                [AccessTokens.TokenId] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(AccessTokens.SigningKey(jwt), SecurityAlgorithms.HmacSha256),
        });
        return new AccessToken(token, expires);
    }

    /// <summary>What the dashboard shows for a user: the display name, else the email, else the id (as <see cref="UserGate"/>).</summary>
    public static string DisplayNameOf(AppUser user) =>
        string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? user.Id : user.DisplayName;
}
