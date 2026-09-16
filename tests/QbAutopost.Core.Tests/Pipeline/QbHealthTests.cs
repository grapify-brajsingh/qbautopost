using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Pipeline;

/// <summary>FR-16 QuickBooks health and the HostQuery request.</summary>
public sealed class QbHealthTests
{
    private static readonly PipelineOptions Options = new()
    {
        CompanyName = "Tropicana Properties LLC",
        RulesFile = "unused",
        LedgerFile = "unused",
        QbListsFile = "unused",
    };

    private static Task<QbHealthResult> Check(HostGateway gateway) => new QbHealth(Options, gateway).CheckAsync(CancellationToken.None);

    [Fact]
    public void Should_MatchGolden_When_BuildingHostQuery()
    {
        Assert.Equal(
            Fixtures.Read("qbxml", "host-query.golden.xml").ReplaceLineEndings("\n").TrimEnd(),
            QbHostQuery.Build().ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void Should_ReadProductAndVersion_When_ParsingHostResponse()
    {
        var host = QbHostQuery.Parse(Fixtures.Read("qbxml", "host-response.xml"));

        Assert.Equal((0, "QuickBooks Desktop Pro 2023 33.0"), (host.Status.Code, host.Product));
    }

    [Fact]
    public async Task Should_BeOkWithProductAndCompanyFile_When_HostQuerySucceeds()
    {
        var result = await Check(new HostGateway { Response = Fixtures.Read("qbxml", "host-response.xml") });

        Assert.Equal(new QbHealthResult(true, @"C:\QB\Tropicana.QBW", "QuickBooks Desktop Pro 2023 33.0"), result);
    }

    [Fact]
    public async Task Should_NotBeOk_When_HostQueryReturnsAnError()
    {
        var result = await Check(new HostGateway
        {
            Response = "<QBXML><QBXMLMsgsRs><HostQueryRs statusCode=\"500\" statusMessage=\"denied\" /></QBXMLMsgsRs></QBXML>",
        });

        Assert.False(result.Ok);
        Assert.Equal("HostQuery failed: 500: denied", result.Message);
    }

    [Fact]
    public async Task Should_NotBeOkAndNameBitness_When_QuickBooksIsUnavailable()
    {
        var result = await Check(new HostGateway { Throw = new QuickBooksUnavailableException("QBXMLRP2.RequestProcessor is not registered") });

        Assert.False(result.Ok);
        Assert.Null(result.CompanyFile);
        Assert.Matches(@"not registered \(process is x(64|86)\)$", result.Message);
    }

    [Fact]
    public async Task Should_NotBeOk_When_ResponseIsUnreadable()
    {
        var result = await Check(new HostGateway { Response = "<QBXML />" });

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Should_StayOkWithoutCompanyFile_When_OnlyTheFileNameFails()
    {
        var result = await Check(new HostGateway
        {
            Response = Fixtures.Read("qbxml", "host-response.xml"),
            CompanyFileThrow = new QuickBooksUnavailableException("no file"),
        });

        Assert.True(result.Ok);
        Assert.Null(result.CompanyFile);
        Assert.Contains("no file", result.Message, StringComparison.Ordinal);
    }

    private sealed class HostGateway : IQbGateway
    {
        public string Response { get; init; } = "";

        public Exception? Throw { get; init; }

        public Exception? CompanyFileThrow { get; init; }

        public Task<string> ProcessAsync(string qbxml, CancellationToken ct) =>
            Throw is null ? Task.FromResult(Response) : Task.FromException<string>(Throw);

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
            CompanyFileThrow is null ? Task.FromResult(@"C:\QB\Tropicana.QBW") : Task.FromException<string>(CompanyFileThrow);
    }
}
