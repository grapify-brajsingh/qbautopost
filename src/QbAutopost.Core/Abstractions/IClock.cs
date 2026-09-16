namespace QbAutopost.Core.Abstractions;

/// <summary>Time source, replaceable in tests (plan §1).</summary>
public interface IClock
{
    DateTime UtcNow { get; }

    DateOnly Today { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}
