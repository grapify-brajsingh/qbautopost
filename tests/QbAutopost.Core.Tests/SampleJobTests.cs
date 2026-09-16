using QbAutopost.Core.Extract;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests;

/// <summary>The synthetic sample job end to end through the Core pieces (no Hermes, no QuickBooks).</summary>
public sealed class SampleJobTests
{
    private static IReadOnlyList<MappedTxn> MapSampleJob()
    {
        var rules = Fixtures.SampleRules();
        var parser = new CsvStatementParser(rules.CsvLayouts);
        var lines = new[] { Fixtures.SampleBankCsv, Fixtures.SampleCardCsv }
            .Select(parser.Parse)
            .SelectMany(r => r.Rows);
        return new Mapper(rules).MapAll(lines);
    }

    [Fact]
    public void Should_PostEightHoldTwoSkipOne_When_MappingSampleJob()
    {
        var txns = MapSampleJob();

        Assert.Equal(8, txns.Count(t => t.Decision == Decision.Post));
        Assert.Equal(2, txns.Count(t => t.Decision == Decision.Hold));
        Assert.Equal(1, txns.Count(t => t.Decision == Decision.Skip));
    }

    [Fact]
    public void Should_HoldOnlyUnknownPayees_When_MappingSampleJob()
    {
        var held = MapSampleJob().Where(t => t.Decision == Decision.Hold).ToList();

        Assert.All(held, t => Assert.Equal(HoldReasons.UnknownPayee, t.Reason));
        Assert.Equal(
            ["ACH DEBIT UNKNOWN PLUMBER SVC", "DEPOSIT SUNRISE PROPERTY MGMT"],
            held.Select(t => t.Line.Description).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Should_ReconcileBothStatements_When_ParsingSampleJob()
    {
        var parser = new CsvStatementParser(Fixtures.SampleRules().CsvLayouts);

        var bank = ReconcileGate.CheckBalanceChain(parser.Parse(Fixtures.SampleBankCsv).Rows);
        var card = ReconcileGate.CheckBalanceChain(parser.Parse(Fixtures.SampleCardCsv).Rows);

        Assert.True(bank is { Ok: true, Verified: true }, bank.Message);
        Assert.True(card is { Ok: true, Verified: false }, card.Message);
    }

    [Fact]
    public void Should_BuildOneRequestPerPostableLine_When_SampleJobIsMapped()
    {
        var postable = MapSampleJob().Where(t => t.Decision == Decision.Post).ToList();

        var xml = QbXmlBuilder.BuildAddRequest(postable);

        Assert.Equal(8, postable.Select(t => t.RequestId).Distinct().Count());
        Assert.Equal(8, CountOccurrences(xml, "requestID=\""));
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
}
