namespace QbAutopost.Api.Database;

/// <summary>Whether the saved-requests feature can run (spec 5.5, 10.7). Everything but <see cref="Ready"/> answers 503.</summary>
public enum DatabaseState
{
    Ready,
    NotConfigured,
    Unreachable,
    SchemaOutOfDate,
}
