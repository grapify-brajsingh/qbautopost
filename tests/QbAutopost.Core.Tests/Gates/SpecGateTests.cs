using QbAutopost.Core.Gates;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Core.Tests.Gates;

/// <summary>Gate G2 negatives and positives (spec FR-2).</summary>
public sealed class SpecGateTests
{
    private const string Company = "Tropicana Properties LLC";

    private static readonly Rules Rules = Rules.Parse("""{ "BankAccounts": { "4521": "Checking" }, "CardAccounts": { "7788": "Card" } }""");

    private static JobSpec Spec(string? company = Company, string[]? bank = null, string[]? card = null, TxnKind[]? kinds = null) => new()
    {
        Company = company,
        Kinds = kinds ?? [TxnKind.Check, TxnKind.CcCharge, TxnKind.CcCredit, TxnKind.Deposit],
        BankLast4 = bank ?? ["4521"],
        CardLast4 = card ?? ["7788"],
    };

    private static StatementSummary Statement(string? last4) => new() { File = $"s-{last4}.csv", Last4 = last4 };

    private static readonly StatementSummary[] BothStatements = [Statement("4521"), Statement("7788")];

    [Fact]
    public void Should_Pass_When_RequirementMatchesFolder()
    {
        var result = SpecGate.Check(Spec(), Company, BothStatements, Rules);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Should_Pass_When_CompanyDiffersOnlyInPunctuationAndCase()
    {
        Assert.True(SpecGate.Check(Spec("TROPICANA PROPERTIES, LLC"), Company, BothStatements, Rules).Ok);
    }

    [Fact]
    public void Should_Pass_When_RequirementHasNoCompany()
    {
        Assert.True(SpecGate.Check(Spec(company: null), Company, BothStatements, Rules).Ok);
    }

    [Fact]
    public void Should_Fail_When_CompanyIsNotConfigured()
    {
        Assert.False(SpecGate.Check(Spec(), "", BothStatements, Rules).Ok);
    }

    [Fact]
    public void Should_Fail_When_RequirementNamesAnotherCompany()
    {
        Assert.False(SpecGate.Check(Spec("Palm Court Rentals LLC"), Company, BothStatements, Rules).Ok);
    }

    [Fact]
    public void Should_Fail_When_NoKindsResolved()
    {
        Assert.False(SpecGate.Check(Spec(kinds: []), Company, BothStatements, Rules).Ok);
    }

    [Fact]
    public void Should_Fail_When_StatedLast4HasNoStatement()
    {
        var result = SpecGate.Check(Spec(), Company, [Statement("4521")], Rules);

        Assert.Contains(result.Errors, e => e.Contains("7788", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_Fail_When_StatementLast4IsNeitherStatedNorRegistered()
    {
        var result = SpecGate.Check(Spec(), Company, [.. BothStatements, Statement("1111")], Rules);

        Assert.Contains(result.Errors, e => e.Contains("1111", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_Pass_When_UnstatedStatementIsRegisteredInRules()
    {
        var result = SpecGate.Check(Spec(card: []), Company, BothStatements, Rules);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Should_IgnoreStatement_When_ItsLast4IsUnknown()
    {
        var result = SpecGate.Check(Spec(), Company, [.. BothStatements, Statement(null)], Rules);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }
}
