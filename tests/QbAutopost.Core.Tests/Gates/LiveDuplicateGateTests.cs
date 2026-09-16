using System.Xml.Linq;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Gates;

/// <summary>Gate G4, QuickBooks half (FR-8).</summary>
public sealed class LiveDuplicateGateTests : IDisposable
{
    private const string Checking = "Chase Checking 4521";
    private const string Card = "Chase Sapphire 7788";
    private const int Window = 3;

    private readonly TempJobFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private static DateOnly D(string iso) => DateOnly.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static MappedTxn Check(decimal amount, string date, string? payee = "Florida Power & Light", string refNumber = "ACH", string account = Checking) => new()
    {
        Line = Lines.Bank(Direction.Debit, amount, $"ACH DEBIT {amount}", date: date),
        Kind = TxnKind.Check,
        Decision = Decision.Post,
        Confidence = Confidence.Rule,
        Account = account,
        Payee = payee,
        LineAccount = "Utilities",
        RefNumber = refNumber,
    };

    private static ExistingTxn Existing(decimal amount, string date, string? payee = null, string? refNumber = null) => new()
    {
        TxnId = "TX-" + date,
        Date = D(date),
        Amount = amount,
        Account = Checking,
        Payee = payee,
        RefNumber = refNumber,
    };

    [Fact]
    public void Should_KeepLine_When_NothingHasTheSameAmount()
    {
        var line = Check(311.40m, "2026-08-12");

        Assert.Same(line, LiveDuplicateGate.Decide(line, [Existing(311.41m, "2026-08-12", "Florida Power & Light")], Window));
    }

    [Fact]
    public void Should_KeepLine_When_SameAmountIsOutsideTheWindow()
    {
        var line = Check(311.40m, "2026-08-12");

        Assert.Same(line, LiveDuplicateGate.Decide(line, [Existing(311.40m, "2026-08-16"), Existing(311.40m, "2026-08-08")], Window));
    }

    [Fact]
    public void Should_SkipAlreadyInQuickBooks_When_SameDateAndPayee()
    {
        var decided = LiveDuplicateGate.Decide(Check(311.40m, "2026-08-12"), [Existing(311.40m, "2026-08-12", "FLORIDA POWER & LIGHT")], Window);

        Assert.Equal((Decision.Skip, HoldReasons.AlreadyInQuickBooks), (decided.Decision, decided.Reason));
        Assert.Contains("TX-2026-08-12", decided.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_SkipAlreadyInQuickBooks_When_SameDateAndCheckNumber()
    {
        var decided = LiveDuplicateGate.Decide(
            Check(420m, "2026-08-05", payee: null, refNumber: "1043"), [Existing(420m, "2026-08-05", refNumber: "1043")], Window);

        Assert.Equal(HoldReasons.AlreadyInQuickBooks, decided.Reason);
    }

    [Fact]
    public void Should_HoldPossibleDuplicate_When_OnlyAchRefMatches()
    {
        var decided = LiveDuplicateGate.Decide(
            Check(311.40m, "2026-08-12", payee: null), [Existing(311.40m, "2026-08-12", refNumber: "ACH")], Window);

        Assert.Equal((Decision.Hold, HoldReasons.PossibleDuplicate), (decided.Decision, decided.Reason));
    }

    [Fact]
    public void Should_HoldPossibleDuplicate_When_SamePayeeOnAnotherDateInWindow()
    {
        var decided = LiveDuplicateGate.Decide(Check(311.40m, "2026-08-12"), [Existing(311.40m, "2026-08-15", "Florida Power & Light")], Window);

        Assert.Equal((Decision.Hold, HoldReasons.PossibleDuplicate, Confidence.Hold), (decided.Decision, decided.Reason, decided.Confidence));
        Assert.Contains("TX-2026-08-15 on 2026-08-15", decided.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_HoldPossibleDuplicate_When_SameDateButOtherPayee()
    {
        var decided = LiveDuplicateGate.Decide(Check(311.40m, "2026-08-12"), [Existing(311.40m, "2026-08-12", "Someone Else")], Window);

        Assert.Equal(HoldReasons.PossibleDuplicate, decided.Reason);
    }

    [Fact]
    public async Task Should_QueryOncePerKindAndAccount_When_Checking()
    {
        var gateway = new QueryGateway();
        var lines = new[]
        {
            Check(10m, "2026-08-02"),
            Check(20m, "2026-08-20", account: "chase checking 4521"),
            Check(30m, "2026-08-10", account: Card) with { Kind = TxnKind.CcCharge },
        };

        await new LiveDuplicateGate(gateway, "13.0", Window).CheckAsync(lines, _dir.Folder, CancellationToken.None);

        Assert.Equal(2, gateway.Requests.Count);
        var first = XDocument.Parse(gateway.Requests[0]).Descendants("TxnDateRangeFilter").Single();
        Assert.Equal(("2026-07-30", "2026-08-23"), (first.Element("FromTxnDate")!.Value, first.Element("ToTxnDate")!.Value));
        Assert.Contains("<CreditCardChargeQueryRq", gateway.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_NotQuery_When_NoLineIsPostable()
    {
        var gateway = new QueryGateway();
        var held = Check(10m, "2026-08-02") with { Decision = Decision.Hold };

        var result = await new LiveDuplicateGate(gateway, "13.0", Window).CheckAsync([held], _dir.Folder, CancellationToken.None);

        Assert.Empty(gateway.Requests);
        Assert.Same(held, Assert.Single(result));
    }

    [Fact]
    public async Task Should_WriteQueryAuditFilesAndRemoveOldOnes_When_Checking()
    {
        File.WriteAllText(Path.Combine(_dir.Folder, "query-7.qbxml"), "stale");
        var gateway = new QueryGateway();

        await new LiveDuplicateGate(gateway, "13.0", Window).CheckAsync([Check(10m, "2026-08-02")], _dir.Folder, CancellationToken.None);

        Assert.Equal(
            ["query-1.qbxml", "query-1.response.qbxml"],
            Directory.GetFiles(_dir.Folder, "query-*").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(gateway.Requests[0], File.ReadAllText(Path.Combine(_dir.Folder, "query-1.qbxml")));
    }

    [Fact]
    public async Task Should_DecideFromQueryAnswer_When_QuickBooksHasTheLine()
    {
        var gateway = new QueryGateway { Answer = Fixtures.Read("qbxml", "check-query-response.xml") };
        var fpl = Check(311.40m, "2026-08-12");
        var other = Check(99m, "2026-08-12");

        var result = await new LiveDuplicateGate(gateway, "13.0", Window).CheckAsync([fpl, other], _dir.Folder, CancellationToken.None);

        Assert.Equal(HoldReasons.AlreadyInQuickBooks, result[0].Reason);
        Assert.Same(other, result[1]);
    }

    [Fact]
    public async Task Should_HoldGroupDuplicateCheckFailed_When_QueryIsRefused()
    {
        var gateway = new QueryGateway { Answer = "<QBXML><QBXMLMsgsRs><CheckQueryRs statusCode=\"3100\" statusMessage=\"bad\" /></QBXMLMsgsRs></QBXML>" };

        var result = await new LiveDuplicateGate(gateway, "13.0", Window).CheckAsync([Check(10m, "2026-08-02")], _dir.Folder, CancellationToken.None);

        var line = Assert.Single(result);
        Assert.Equal((Decision.Hold, HoldReasons.DuplicateCheckFailed), (line.Decision, line.Reason));
    }

    [Fact]
    public async Task Should_Propagate_When_QuickBooksIsUnavailable()
    {
        var gateway = new QueryGateway { Throw = new QuickBooksUnavailableException("down") };

        await Assert.ThrowsAsync<QuickBooksUnavailableException>(
            () => new LiveDuplicateGate(gateway, "13.0", Window).CheckAsync([Check(10m, "2026-08-02")], _dir.Folder, CancellationToken.None));
    }

    private sealed class QueryGateway : IQbGateway
    {
        public List<string> Requests { get; } = [];

        public string? Answer { get; init; }

        public Exception? Throw { get; init; }

        public Task<string> ProcessAsync(string qbxml, CancellationToken ct)
        {
            Requests.Add(qbxml);
            if (Throw is not null)
            {
                throw Throw;
            }

            var element = XDocument.Parse(qbxml).Root!.Element("QBXMLMsgsRq")!.Elements().Single().Name.LocalName;
            return Task.FromResult(Answer ?? $"<QBXML><QBXMLMsgsRs><{element[..^2]}Rs statusCode=\"1\" /></QBXMLMsgsRs></QBXML>");
        }

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromResult("");
    }
}
