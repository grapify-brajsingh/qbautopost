using System.Text;

namespace QbAutopost.Api.Configuration;

/// <summary>
/// Saved-requests spec 4.2, the <c>Auth</c> section. <see cref="JwtSettings.SigningKey"/> and
/// <see cref="BootstrapAdminSettings.Password"/> are environment only (CLAUDE.md rule 6); <c>SecretWarnings</c> names
/// either when it is read from a settings file.
/// </summary>
public sealed class AuthSettings
{
    public JwtSettings Jwt { get; set; } = new();

    public LockoutSettings Lockout { get; set; } = new();

    public PasswordSettings Password { get; set; } = new();

    public BootstrapAdminSettings BootstrapAdmin { get; set; } = new();
}

public sealed class JwtSettings
{
    /// <summary>HS256 needs a key of at least this many bytes; a shorter one leaves Bearer sign-in off (503).</summary>
    public const int MinSigningKeyBytes = 32;

    public string Issuer { get; set; } = "qbautopost";

    public string Audience { get; set; } = "qbautopost-dashboard";

    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>
    /// T-1115 (dashboard v2 spec 11): how long after its rotation a refresh token may come back once to the v2 refresh
    /// and get a second successor. v1 refresh has no grace. Zero or less gives none.
    /// </summary>
    public int RefreshReuseGraceSeconds { get; set; } = 30;

    /// <summary>False when the key is empty or shorter than <see cref="MinSigningKeyBytes"/>: API keys keep working, Bearer answers 503.</summary>
    public bool HasUsableKey => Encoding.UTF8.GetByteCount(SigningKey) >= MinSigningKeyBytes;
}

public sealed class LockoutSettings
{
    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;
}

public sealed class PasswordSettings
{
    public int MinLength { get; set; } = 12;
}

public sealed class BootstrapAdminSettings
{
    public string Email { get; set; } = "";

    public string Password { get; set; } = "";
}
