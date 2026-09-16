using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.TestSupport;

public sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    public DateOnly Today => DateOnly.FromDateTime(UtcNow);
}
