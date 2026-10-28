namespace QbAutopost.Data.Tests;

/// <summary>A fact that needs a real PostgreSQL; skipped with the reason when Docker cannot provide one.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PostgresFixture.SharedSkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
