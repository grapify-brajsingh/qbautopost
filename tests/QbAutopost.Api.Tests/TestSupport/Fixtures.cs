namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>Files copied next to the test assembly from <c>tests/fixtures</c> and <c>samples</c>.</summary>
public static class Fixtures
{
    public static string SampleJob { get; } = Path.Combine(AppContext.BaseDirectory, "samples", "jobs", "2026-08-tropicana");

    public static string SampleRules { get; } = Path.Combine(AppContext.BaseDirectory, "samples", "rules.json");

    /// <summary>The POC job that posts every line by rule, without Hermes (<c>samples/poc</c>).</summary>
    public static string PocJob { get; } = Path.Combine(AppContext.BaseDirectory, "samples", "poc", "jobs", "2026-09-tropicana");

    /// <summary>The POC job with a single XLSX bank statement and no card file.</summary>
    public static string PocXlsxJob { get; } = Path.Combine(AppContext.BaseDirectory, "samples", "poc", "jobs", "2026-08-tropicana-xlsx");

    public static string PocRules { get; } = Path.Combine(AppContext.BaseDirectory, "samples", "poc", "rules.json");

    /// <summary>QuickBooks IIF import with the accounts, vendors and customers the POC rules name.</summary>
    public static string PocLists { get; } = Path.Combine(AppContext.BaseDirectory, "samples", "poc", "tropicana-lists.iif");

    public static string PathOf(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);
}
