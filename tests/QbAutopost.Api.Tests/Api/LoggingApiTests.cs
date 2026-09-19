using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Tests.Api;

/// <summary>Spec §14 through the host: rolling file under Paths:Logs, jobId on every line, no secrets.</summary>
public sealed class LoggingApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public LoggingApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private string LogDir => _factory.Dir.Combine("data", "logs");

    private string[] LogLines()
    {
        var file = Assert.Single(Directory.GetFiles(LogDir, LoggingSetup.FilePrefix + "*.log"));
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task Should_WriteDailyFileUnderLogsPath_When_HostStarts()
    {
        using var response = await _client.GetAsync("/health/hermes");

        var file = Path.GetFileName(Assert.Single(Directory.GetFiles(LogDir)));
        Assert.Matches(@"^qbautopost-\d{8}\.log$", file);
        Assert.Contains(LogLines(), l => l.Contains("QuickBooks gateway", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_TagEveryJobLineWithJobId_When_JobRuns()
    {
        var view = await _client.RunToEndAsync(_factory.Dir.CopySampleJob());
        Assert.Equal(JobStatus.Ready, view.Status);

        var jobLines = LogLines().Where(l => l.Contains("Job 2026-08-tropicana", StringComparison.Ordinal)).ToList();
        Assert.True(jobLines.Count >= 2, string.Join('\n', LogLines()));
        Assert.All(jobLines, l => Assert.Contains("[2026-08-tropicana]", l, StringComparison.Ordinal));
    }

    [Fact]
    public void Should_TagOtherLinesWithDash_When_NoJobIsInvolved()
    {
        _ = _factory.Services;

        var startup = Assert.Single(LogLines(), l => l.Contains("QuickBooks gateway", StringComparison.Ordinal));
        Assert.Contains("[-]", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MaskApiKey_When_HostLogsIt()
    {
        var log = _factory.Services.GetRequiredService<ILogger<LoggingApiTests>>();

        log.LogWarning("received {Header}", "key " + ApiFactory.ApiKey);

        var line = Assert.Single(LogLines(), l => l.Contains("received", StringComparison.Ordinal));
        Assert.DoesNotContain(ApiFactory.ApiKey, line, StringComparison.Ordinal);
        Assert.Contains("key ***", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_LogProcessAndSettings_When_HostStarts()
    {
        _ = _factory.Services;

        var text = string.Join('\n', LogLines());
        Assert.Matches(@"(x64|x86) process", text);
        Assert.Contains("DryRunDefault true", text, StringComparison.Ordinal);
        Assert.Contains(ApiFactory.Company, text, StringComparison.Ordinal);
        Assert.Contains("busy timeout", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.ApiKey, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_LogEachRequestWithStatus_When_ApiIsCalled()
    {
        using var response = await _client.GetAsync("/jobs");

        Assert.Contains(LogLines(), l => l.Contains("HTTP GET /jobs responded 200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LogRejectedKeyWithoutItsValue_When_KeyIsWrong()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key-98765");

        using var response = await client.GetAsync("/jobs");

        var text = string.Join('\n', LogLines());
        Assert.Contains("invalid X-Api-Key", text, StringComparison.Ordinal);
        Assert.Contains("HTTP GET /jobs responded 401", text, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-key-98765", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_LogAcceptanceStatementsAndHeldLines_When_JobIsAnalysed()
    {
        var view = await _client.RunToEndAsync(_factory.Dir.CopySampleJob());

        var text = string.Join('\n', LogLines());
        Assert.Contains("Job 2026-08-tropicana: accepted", text, StringComparison.Ordinal);
        Assert.Contains("statement chase-checking-4521.csv", text, StringComparison.Ordinal);
        var held = Assert.IsType<string>(view.Held[0].Reason);
        Assert.Contains(LogLines(), l => l.Contains("held", StringComparison.Ordinal) && l.Contains(held, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_KeepStatementDescriptionsOutOfInformationLog_When_JobRuns()
    {
        await _client.RunToEndAsync(_factory.Dir.CopySampleJob(), dryRun: false);

        Assert.DoesNotContain(LogLines(), l => l.Contains("ACH DEBIT FPL ELECTRIC UTILITY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LogQuickBooksCallsAndPostedTxnIds_When_JobPosts()
    {
        var view = await _client.RunToEndAsync(_factory.Dir.CopySampleJob(), dryRun: false);

        var txnId = view.Posted[0].TxnId;
        var text = string.Join('\n', LogLines());
        Assert.Contains("QuickBooks call", text, StringComparison.Ordinal);
        Assert.Contains("CheckAddRq x", text, StringComparison.Ordinal);
        Assert.Contains(LogLines(), l => l.Contains("posted", StringComparison.Ordinal) && l.Contains(txnId, StringComparison.Ordinal));
        Assert.Contains("batch 2026-08-tropicana#1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MaskHermesKey_When_ItComesFromConfiguration()
    {
        using var host = _factory.WithSetting("Hermes:ApiKey", "sk-provider-0042");
        var log = host.Services.GetRequiredService<ILogger<LoggingApiTests>>();

        log.LogError(new HttpRequestException("401 for key sk-provider-0042"), "Hermes call failed");

        var text = string.Join('\n', LogLines());
        Assert.Contains("Hermes call failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-provider-0042", text, StringComparison.Ordinal);
    }
}
