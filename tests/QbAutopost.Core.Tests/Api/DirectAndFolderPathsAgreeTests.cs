using QbAutopost.Core.Api;
using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Api;

/// <summary>
/// T-906 / api-v1 §6: "one engine, two entrances". A transaction sent as JSON and the same transaction read off a
/// statement must be indistinguishable by the time they reach QuickBooks — same fingerprint, so G4 catches a repeat
/// across both paths, and byte-identical qbXML, so FR-9's element order cannot drift between them.
/// <para>
/// Without this test the two paths could diverge silently: a caller could post a duplicate of a line already entered
/// from a statement, and nothing would notice.
/// </para>
/// </summary>
public sealed class DirectAndFolderPathsAgreeTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static MappedTxn Post(StatementLine line, TxnKind kind) => new()
    {
        Line = line,
        Kind = kind,
        Decision = Decision.Post,
        Confidence = Confidence.Rule,
        Account = "Chase Checking 4521",
        Payee = "Florida Power & Light",
        LineAccount = "Utilities",
        RefNumber = "ACH",
    };

    private static StatementLine FromDirect(TxnKind kind, decimal amount, string description, string? checkNo = null)
    {
        var request = new DirectRequest
        {
            ControlTotal = amount,
            Transactions =
            [
                new DirectRow
                {
                    Kind = kind,
                    Date = new DateOnly(2026, 8, 12),
                    Amount = amount,
                    Account = "Chase Checking 4521",
                    Payee = "Florida Power & Light",
                    LineAccount = "Utilities",
                    Memo = description,
                    CheckNo = checkNo,
                    Last4 = "4521",
                },
            ],
        };

        var result = new DirectRequestReader(new DirectLimits()).Read(request, Today);
        Assert.Empty(result.Errors);
        return Assert.Single(result.Lines).Line;
    }

    [Fact]
    public void Should_FingerprintAlike_When_ACheckArrivesByEitherPath()
    {
        var fromStatement = Lines.Bank(Direction.Debit, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY", date: "2026-08-12") with
        {
            Last4 = "4521",
        };
        var fromApi = FromDirect(TxnKind.Check, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY");

        // G4 compares fingerprints, so this is what stops a caller re-posting a line already entered from a statement.
        Assert.Equal(fromStatement.Fingerprint, fromApi.Fingerprint);
    }

    [Fact]
    public void Should_BuildIdenticalQbXml_When_ACheckArrivesByEitherPath()
    {
        var fromStatement = Lines.Bank(Direction.Debit, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY", date: "2026-08-12") with
        {
            Last4 = "4521",
        };
        var fromApi = FromDirect(TxnKind.Check, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY");

        Assert.Equal(
            QbXmlBuilder.BuildAddRequest([Post(fromStatement, TxnKind.Check)]),
            QbXmlBuilder.BuildAddRequest([Post(fromApi, TxnKind.Check)]));
    }

    [Fact]
    public void Should_BuildIdenticalQbXml_When_ACardChargeArrivesByEitherPath()
    {
        var fromStatement = Lines.Card(Direction.Debit, 62.18m, "AMAZON.COM*RT4Y1 AMZN.COM/BILL", date: "2026-08-12") with
        {
            Last4 = "4521",
        };
        var fromApi = FromDirect(TxnKind.CcCharge, 62.18m, "AMAZON.COM*RT4Y1 AMZN.COM/BILL");

        Assert.Equal(fromStatement.Fingerprint, fromApi.Fingerprint);
        Assert.Equal(
            QbXmlBuilder.BuildAddRequest([Post(fromStatement, TxnKind.CcCharge)]),
            QbXmlBuilder.BuildAddRequest([Post(fromApi, TxnKind.CcCharge)]));
    }

    [Fact]
    public void Should_KeepTheCheckNumberInTheFingerprint_When_OneIsGiven()
    {
        var withNumber = FromDirect(TxnKind.Check, 420m, "CHECK 1043", checkNo: "1043");
        var withoutNumber = FromDirect(TxnKind.Check, 420m, "CHECK 1043");

        // Spec §7 includes checkNo, so two cheques for the same amount on the same day stay distinct.
        Assert.NotEqual(withNumber.Fingerprint, withoutNumber.Fingerprint);
    }
}
