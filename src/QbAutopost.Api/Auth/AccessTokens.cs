using System.Text;
using Microsoft.IdentityModel.Tokens;
using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Auth;

/// <summary>
/// Saved-requests spec 11.2: the dashboard access token is an HS256 JWT carrying <c>sub</c>, <c>email</c>,
/// <c>name</c>, <c>role</c>, <c>stamp</c> and <c>jti</c>. One place for the claim names and the validation rules, so
/// what T-1010 issues is exactly what <c>CallerMiddleware</c> accepts.
/// </summary>
public static class AccessTokens
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string Stamp = "stamp";
    public const string TokenId = "jti";

    public static SymmetricSecurityKey SigningKey(JwtSettings jwt) => new(Encoding.UTF8.GetBytes(jwt.SigningKey));

    /// <summary>Issuer, audience, lifetime with no clock skew (one machine issues and checks), HS256 only.</summary>
    public static TokenValidationParameters ValidationParameters(JwtSettings jwt) => new()
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = SigningKey(jwt),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = TimeSpan.Zero,
        NameClaimType = Name,
        RoleClaimType = Role,
    };
}
