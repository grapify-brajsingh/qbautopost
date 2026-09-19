using Microsoft.Extensions.Logging;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.Tests.QuickBooks;

public sealed class LoggingQbGatewayTests
{
    private static readonly string CheckRequest = File.ReadAllText(Fixtures.PathOf("qbxml", "check.golden.xml"));
    private static readonly string AddResponse = File.ReadAllText(Fixtures.PathOf("qbxml", "add-response.xml"));

    private readonly ListLogger<LoggingQbGateway> _log = new();

    private LoggingQbGateway Wrap(IQbGateway inner, double busySeconds = 60) =>
        new(inner, _log, TimeSpan.FromSeconds(busySeconds));

    [Fact]
    public async Task Should_LogWhatIsSentAndTheAnswer_When_CallSucceeds()
    {
        await Wrap(new StubGateway(AddResponse)).ProcessAsync(CheckRequest, CancellationToken.None);

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("sending CheckAddRq x", StringComparison.Ordinal));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("3 responses", StringComparison.Ordinal));
        Assert.Contains(_log.Entries, e => e.Message.Contains("CheckAddRs", StringComparison.Ordinal) && e.Message.Contains("1A2B-1787000001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LogWarningWithStatus_When_QuickBooksRefusesARequest()
    {
        await Wrap(new StubGateway(AddResponse)).ProcessAsync(CheckRequest, CancellationToken.None);

        var warning = Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("CreditCardChargeAddRs", warning.Message, StringComparison.Ordinal);
        Assert.Contains("3140", warning.Message, StringComparison.Ordinal);
        Assert.Contains("invalid reference", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_NotLogTheRequestBody_When_Sending()
    {
        await Wrap(new StubGateway(AddResponse)).ProcessAsync(CheckRequest, CancellationToken.None);

        Assert.DoesNotContain("ACH DEBIT FPL ELECTRIC UTILITY", _log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_LogErrorWithCodeAndRethrow_When_CallFails()
    {
        var failure = new QuickBooksCallException("ProcessRequest failed (0x80040408: boom)", unchecked((int)0x80040408));

        var thrown = await Assert.ThrowsAsync<QuickBooksCallException>(
            () => Wrap(new StubGateway(failure)).ProcessAsync(CheckRequest, CancellationToken.None));

        Assert.Same(failure, thrown);
        var error = Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("0x80040408", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_LogWarningAndRethrow_When_QuickBooksIsUnavailable()
    {
        var failure = new QuickBooksUnavailableException("could not open a QuickBooks session");

        await Assert.ThrowsAsync<QuickBooksUnavailableException>(
            () => Wrap(new StubGateway(failure)).ProcessAsync(CheckRequest, CancellationToken.None));

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not open a QuickBooks session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_WarnAboutBusyTimeout_When_CallTakesLongerThanIt()
    {
        var slow = new StubGateway(AddResponse) { Delay = TimeSpan.FromMilliseconds(80) };

        await Wrap(slow, busySeconds: 0.01).ProcessAsync(CheckRequest, CancellationToken.None);

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("busy timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LogTheCompanyFile_When_Asked()
    {
        var file = await Wrap(new StubGateway(AddResponse) { CompanyFile = @"C:\qb\Copy.QBW" }).CurrentCompanyFileAsync(CancellationToken.None);

        Assert.Equal(@"C:\qb\Copy.QBW", file);
        Assert.Contains(_log.Entries, e => e.Message.Contains(@"C:\qb\Copy.QBW", StringComparison.Ordinal));
    }

    private sealed class StubGateway : IQbGateway
    {
        private readonly string? _response;
        private readonly Exception? _failure;

        public StubGateway(string response) => _response = response;

        public StubGateway(Exception failure) => _failure = failure;

        public TimeSpan Delay { get; init; }

        public string CompanyFile { get; init; } = "";

        public async Task<string> ProcessAsync(string qbxml, CancellationToken ct)
        {
            await Task.Delay(Delay, ct);
            return _failure is null ? _response! : throw _failure;
        }

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromResult(CompanyFile);
    }
}
