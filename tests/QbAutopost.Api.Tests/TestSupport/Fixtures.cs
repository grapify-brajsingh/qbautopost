namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>Files copied next to the test assembly from <c>tests/fixtures</c> and <c>samples</c>.</summary>
public static class Fixtures
{
    public static string SampleJob { get; } = Path.Combine(AppContext.BaseDirectory, "samples", "jobs", "2026-08-tropicana");

    public static string SampleRules { get; } = Path.Combine(AppContext.BaseDirectory, "samples", "rules.json");

    public static string PathOf(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);
}
