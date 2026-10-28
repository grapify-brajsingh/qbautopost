namespace QbAutopost.Api.Configuration;

/// <summary>
/// Saved-requests spec 4.2. <see cref="ConnectionString"/> is environment only
/// (<c>QBAUTOPOST__Database__ConnectionString</c>, CLAUDE.md rule 6); empty means the feature is off and every
/// saved-request route answers 503 while the rest of the API runs as before.
/// </summary>
public sealed class DatabaseSettings
{
    public string ConnectionString { get; set; } = "";

    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>When true (spec 5.5), a database whose applied migrations differ from this build is not ready.</summary>
    public bool RequireCurrentSchema { get; set; } = true;

    public bool Configured => !string.IsNullOrWhiteSpace(ConnectionString);
}
