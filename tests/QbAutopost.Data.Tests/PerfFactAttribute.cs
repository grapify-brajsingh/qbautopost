namespace QbAutopost.Data.Tests;

/// <summary>A PostgreSQL test that also needs <c>QBAUTOPOST_PERF=1</c>; otherwise it is skipped.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("QBAUTOPOST_PERF") != "1")
        {
            Skip = "Performance test: set QBAUTOPOST_PERF=1 to run.";
        }
        else if (PostgresFixture.SharedSkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
