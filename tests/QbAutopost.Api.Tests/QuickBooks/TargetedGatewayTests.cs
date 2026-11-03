using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Gateway;
using QbAutopost.Core.QbXml;

namespace QbAutopost.Api.Tests.QuickBooks;

/// <summary>M17 §4.7: one gateway, the company file chosen per call.</summary>
public sealed class TargetedGatewayTests
{
    private const string SunAmericaFile = @"C:\fake\SA.QBW";
    private static readonly QbTarget Takoma = new(@"C:\fake\Takoma.QBW");
    private static readonly string Query = QbListQuery.Build();

    private static ResilientQbGateway Wrap(IQbGateway inner) =>
        new(
            new LoggingQbGateway(inner, new ListLogger<LoggingQbGateway>(), TimeSpan.FromSeconds(60)),
            new QbGatewayPolicy { BusyTimeout = TimeSpan.FromSeconds(10) },
            (_, _) => Task.CompletedTask);

    private static async Task<IReadOnlyList<string>> VendorsAsync(IQbGateway gateway, QbTarget target) =>
        QbListQuery.Parse(await gateway.ProcessAsync(target, Query, CancellationToken.None), DateTime.UtcNow).Vendors;

    [Fact]
    public async Task Should_ThrowWrongCompanyAndSendNothing_When_AnotherFileIsOpen()
    {
        var fake = new FakeQbGateway { OpenCompanyFile = SunAmericaFile };
        IQbGateway gateway = Wrap(fake);

        var ex = await Assert.ThrowsAsync<QuickBooksWrongCompanyException>(
            () => gateway.ProcessAsync(Takoma, Query, CancellationToken.None));

        Assert.Equal((Takoma.CompanyFile, SunAmericaFile), (ex.NeededFile, ex.OpenFile));
        Assert.Empty(fake.Requests);
        Assert.Equal(1, fake.Refusals);
    }

    [Fact]
    public async Task Should_NotRetry_When_NonDefaultCompanySessionCouldNotOpen()
    {
        var fake = new FakeQbGateway { UnavailableCompanyFile = Takoma.CompanyFile };
        IQbGateway gateway = Wrap(fake);

        var ex = await Assert.ThrowsAsync<QuickBooksCompanyFileUnavailableException>(
            () => gateway.ProcessAsync(Takoma, Query, CancellationToken.None));

        Assert.Equal(Takoma.CompanyFile, ex.NeededFile);
        Assert.Empty(fake.Requests);
        Assert.Equal(1, fake.UnavailableCalls);
    }

    [Fact]
    public async Task Should_AnswerForTheDefaultFile_When_AnotherCompanyFileIsUnavailable()
    {
        var fake = new FakeQbGateway { UnavailableCompanyFile = Takoma.CompanyFile };

        await Wrap(fake).ProcessAsync(Query, CancellationToken.None);

        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Should_AnswerFromEachCompany_When_SimulatedGatewayIsTargeted()
    {
        var simulated = new SimulatedQbGateway(new SimulatedQuickBooks());
        simulated.CompanyFor(Takoma.CompanyFile).Vendors.Add("Takoma Vendor");
        IQbGateway gateway = Wrap(simulated);

        var takoma = await VendorsAsync(gateway, Takoma);
        var sunAmerica = await VendorsAsync(gateway, new QbTarget(SunAmericaFile));

        Assert.Equal(["Takoma Vendor"], takoma);
        Assert.Empty(sunAmerica);
    }

    [Fact]
    public async Task Should_UseTheSeededCompany_When_TargetIsTheDefaultFile()
    {
        var seeded = new SimulatedQuickBooks();
        seeded.Vendors.Add("Default Vendor");
        IQbGateway gateway = Wrap(new SimulatedQbGateway(seeded, @"C:\fake\Tropicana.QBW"));

        var vendors = await VendorsAsync(gateway, new QbTarget(@"c:\FAKE\tropicana.qbw"));

        Assert.Equal(["Default Vendor"], vendors);
    }

    [Fact]
    public async Task Should_ReportTheTarget_When_SimulatedGatewayIsAskedForTheTargetsFile()
    {
        IQbGateway gateway = new SimulatedQbGateway(new SimulatedQuickBooks());

        Assert.Equal(Takoma.CompanyFile, await gateway.CurrentCompanyFileAsync(Takoma, CancellationToken.None));
    }

    [Fact]
    public async Task Should_ReachTheTarget_When_UntargetedCallGoesThroughFor()
    {
        var fake = new FakeQbGateway();

        await Wrap(fake).For(Takoma).ProcessAsync(Query, CancellationToken.None);

        Assert.Equal(new string?[] { Takoma.CompanyFile }, fake.Targets);
    }

    [Fact]
    public void Should_ReturnTheGatewayItself_When_ForHasNoTarget()
    {
        var gateway = Wrap(new FakeQbGateway());

        Assert.Same(gateway, gateway.For(null));
    }

    [Fact]
    public async Task Should_RefuseTargetedCall_When_GatewayServesOneFile()
    {
        var single = new SingleFileGateway();

        await Assert.ThrowsAsync<QuickBooksUnavailableException>(
            () => ((IQbGateway)single).ProcessAsync(Takoma, Query, CancellationToken.None));

        Assert.Equal(0, single.Calls);
    }

    [Fact]
    public async Task Should_RefuseTargetedCall_When_GatewayIsUnconfigured()
    {
        IQbGateway gateway = new UnconfiguredQbGateway();

        var ex = await Assert.ThrowsAsync<QuickBooksUnavailableException>(
            () => gateway.ProcessAsync(Takoma, Query, CancellationToken.None));

        Assert.Contains("only available on Windows", ex.Message, StringComparison.Ordinal);
    }

    private sealed class SingleFileGateway : IQbGateway
    {
        public int Calls { get; private set; }

        public Task<string> ProcessAsync(string qbxml, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("");
        }

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("");
        }
    }
}
