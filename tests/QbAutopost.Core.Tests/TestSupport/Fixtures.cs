using QbAutopost.Core.Mapping;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>Paths to files copied next to the test assembly from <c>tests/fixtures</c> and <c>samples</c>.</summary>
internal static class Fixtures
{
    public static string SampleJob { get; } = Path.Combine(AppContext.BaseDirectory, "samples", "jobs", "2026-08-tropicana");

    public static string SampleBankCsv { get; } = Path.Combine(SampleJob, "statements", "chase-checking-4521.csv");

    public static string SampleCardCsv { get; } = Path.Combine(SampleJob, "statements", "chase-card-7788.csv");

    public static string PathOf(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);

    public static string Read(params string[] parts) => File.ReadAllText(PathOf(parts));

    public static Rules SampleRules() =>
        Rules.Load(Path.Combine(AppContext.BaseDirectory, "samples", "rules.json"));
}
