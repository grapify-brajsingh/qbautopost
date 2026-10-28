namespace QbAutopost.Api.Security;

/// <summary>
/// Saved-requests spec 4.2: a secret found in a JSON settings file is accepted but named, once, at startup - the key,
/// never the value (CLAUDE.md rule 6). Same test as the PFX password: <see cref="TransportGuard.CameFromFile"/>.
/// <c>Api:Tls:PfxPassword</c> keeps its own message in <see cref="TransportGuard"/>.
/// </summary>
public static class SecretWarnings
{
    /// <summary>Settings that are secrets whatever they contain.</summary>
    private static readonly string[] AlwaysSecret = ["Auth:Jwt:SigningKey", "Auth:BootstrapAdmin:Password"];

    private const string ConnectionStringKey = "Database:ConnectionString";

    /// <summary>The keys (never values) that hold a secret read from a settings file.</summary>
    public static IReadOnlyList<string> KeysFoundInFile(IConfiguration config)
    {
        var keys = new List<string>();
        var connection = config[ConnectionStringKey];
        // Only a connection string that carries a password is a secret; a trust or socket-auth one is not.
        if (connection is not null
            && connection.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            && TransportGuard.CameFromFile(config, ConnectionStringKey))
        {
            keys.Add(ConnectionStringKey);
        }

        keys.AddRange(AlwaysSecret.Where(k => TransportGuard.CameFromFile(config, k)));
        return keys;
    }

    public static void Warn(IConfiguration config, ILogger log)
    {
        foreach (var key in KeysFoundInFile(config))
        {
            log.LogWarning(
                "{SettingKey} was read from {SettingsFile}. It is accepted, but it belongs in the "
                + "QBAUTOPOST__{EnvironmentName} environment variable, not in a file that gets copied and backed up.",
                key, TransportGuard.SettingsFileName, key.Replace(":", "__", StringComparison.Ordinal));
        }
    }
}
