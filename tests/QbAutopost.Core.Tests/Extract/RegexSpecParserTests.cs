using QbAutopost.Core.Extract;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Extract;

public sealed class RegexSpecParserTests
{
    [Fact]
    public void Should_MatchFr2AcceptanceCriteria_When_ParsingSampleRequirement()
    {
        var spec = RegexSpecParser.Parse(File.ReadAllText(Path.Combine(Fixtures.SampleJob, "requirement.txt")));

        Assert.Equal([TxnKind.Check, TxnKind.CcCharge, TxnKind.CcCredit, TxnKind.Deposit], spec.Kinds);
        Assert.Equal(["4521"], spec.BankLast4);
        Assert.Equal(["7788"], spec.CardLast4);
        Assert.Equal("Tropicana Properties LLC", spec.Company);
    }

    [Fact]
    public void Should_CaptureFieldRulesPerSection_When_ParsingSampleRequirement()
    {
        var spec = RegexSpecParser.Parse(File.ReadAllText(Path.Combine(Fixtures.SampleJob, "requirement.txt")));

        Assert.Equal("Vendor if ACH – Blank if Check", spec.FieldRules["check"]["Payee"]);
        Assert.Equal("ACH", spec.FieldRules["card"]["Payee"]);
        Assert.Equal("Customer", spec.FieldRules["deposit"]["Received From"]);
    }

    [Fact]
    public void Should_IgnorePlaceholderCompany_When_RequirementIsTheBlankTemplate()
    {
        const string template = """
            Company.>Batch Enter Transactions

            Transactions Type => Checks (Debit Entries)
            Bank Account=From Statement (Last Four Digits)
            Transactions Type => Credit Card
                Credit Card Charges & Credits= From Statement (Last Four)
            Transactions Type > Deposit
                Account From = Bank
            """;

        var spec = RegexSpecParser.Parse(template);

        Assert.Null(spec.Company);
        Assert.Equal(4, spec.Kinds.Count);
        Assert.Empty(spec.BankLast4);
        Assert.Empty(spec.CardLast4);
    }

    [Fact]
    public void Should_ResolveNoKinds_When_TextHasNoTransactionTypes()
    {
        var spec = RegexSpecParser.Parse("Please post August.");

        Assert.Empty(spec.Kinds);
    }
}
