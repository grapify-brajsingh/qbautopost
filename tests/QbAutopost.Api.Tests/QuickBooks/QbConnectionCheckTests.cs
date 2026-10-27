using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Gateway;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Tests.QuickBooks;

/// <summary>
/// T-904 / FR-A-4: the connection check reports every step with its duration, so the session-13 failure — the
/// certificate dialog holding <c>BeginSession</c> for 101 s, past the 60 s busy timeout — shows up as a slow step
/// that still succeeded, instead of a bare "quickbooks-busy".
/// </summary>
public sealed class QbConnectionCheckTests
{
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromMilliseconds(50);

    private static QbConnectionCheck Build(IQbGateway inner) =>
        new(
            new PipelineOptions
            {
                QbXmlVersion = "16.0",
                CompanyName = ApiFactory.Company,
                RulesFile = "rules.json",
                LedgerFile = "ledger.json",
                QbListsFile = "qb-lists.json",
            },
            new ResilientQbGateway(inner, new QbGatewayPolicy { BusyTimeout = BusyTimeout, RetryDelay = TimeSpan.Zero }));

    [Fact]
    public async Task Should_ReportEveryStep_When_TheConnectionWorks()
    {
        var check = Build(new FakeQbGateway());

        var result = await check.RunAsync(TimeSpan.FromSeconds(5), includeCompanyInfo: true, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["waitForGateway", "hostQuery", "companyFile"], result.Steps.Select(s => s.Name));
        Assert.All(result.Steps, s => Assert.True(s.Ok));
        Assert.Equal(@"C:\fake\Tropicana.QBW", result.CompanyFile);
        Assert.True(result.TotalMs >= 0);
    }

    [Fact]
    public async Task Should_SkipTheCompanyFile_When_CompanyInfoIsNotAsked()
    {
        var check = Build(new FakeQbGateway());

        var result = await check.RunAsync(TimeSpan.FromSeconds(5), includeCompanyInfo: false, CancellationToken.None);

        Assert.Equal(["waitForGateway", "hostQuery"], result.Steps.Select(s => s.Name));
        Assert.Null(result.CompanyFile);
    }

    [Fact]
    public async Task Should_MarkTheStepSlow_When_ItTakesLongerThanTheBusyTimeout()
    {
        // The certificate dialog, in miniature: the call succeeds, but well past the busy timeout.
        var check = Build(new SlowGateway(TimeSpan.FromMilliseconds(200)));

        var result = await check.RunAsync(TimeSpan.FromSeconds(10), includeCompanyInfo: false, CancellationToken.None);

        var hostQuery = result.Steps.Single(s => s.Name == "hostQuery");
        Assert.True(hostQuery.Ok, "a slow step still succeeded");
        Assert.True(result.Ok, result.Message);
        Assert.Contains("slow", hostQuery.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dialog", hostQuery.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ReportTheFailingStep_When_QuickBooksIsNotAvailable()
    {
        var check = Build(new FakeQbGateway { QueryThrow = new QuickBooksUnavailableException("nothing is listening") });

        var result = await check.RunAsync(TimeSpan.FromSeconds(5), includeCompanyInfo: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.Steps.Single(s => s.Name == "hostQuery").Ok);
        Assert.Contains("nothing is listening", result.Message);
        // The company file is never asked for once the host query failed.
        Assert.DoesNotContain(result.Steps, s => s.Name == "companyFile");
    }

    [Fact]
    public async Task Should_StillReportOk_When_OnlyTheCompanyFileIsUnreadable()
    {
        // FR-16: HostQuery answering is what "ok" means; a missing file name is reported, not a failure.
        var check = Build(new FakeQbGateway { CompanyFileThrow = new QuickBooksCallException("no company file is open", 0) });

        var result = await check.RunAsync(TimeSpan.FromSeconds(5), includeCompanyInfo: true, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(result.Steps.Single(s => s.Name == "companyFile").Ok);
        Assert.Null(result.CompanyFile);
    }

    /// <summary>Answers like the fake company, but slowly.</summary>
    private sealed class SlowGateway(TimeSpan delay) : IQbGateway
    {
        private readonly FakeQbGateway _inner = new();

        public async Task<string> ProcessAsync(string qbxml, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return await _inner.ProcessAsync(qbxml, ct);
        }

        public async Task<string> CurrentCompanyFileAsync(CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return await _inner.CurrentCompanyFileAsync(ct);
        }
    }
}
