using System.Text.Json;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Tests.Hermes;

public sealed class SpecAnswerTests
{
    [Fact]
    public void Should_MapFixtureToFr2AcceptanceSpec_When_AnswerIsTheSampleFixture()
    {
        var answer = Parse(Fixtures.Read("hermes", "spec.json"));

        Assert.Empty(answer.Validate());
        var spec = answer.ToJobSpec();
        Assert.Equal("Tropicana Properties LLC", spec.Company);
        Assert.Equal([TxnKind.Check, TxnKind.CcCharge, TxnKind.CcCredit, TxnKind.Deposit], spec.Kinds);
        Assert.Equal(["4521"], spec.BankLast4);
        Assert.Equal(["7788"], spec.CardLast4);
        Assert.Equal("Check Number /ACH", spec.FieldRules["check"]["Check"]);
    }

    [Fact]
    public void Should_AgreeWithRegexParser_When_ReadingTheSampleRequirement()
    {
        var hermes = Parse(Fixtures.Read("hermes", "spec.json")).ToJobSpec();
        var regex = RegexSpecParser.Parse(File.ReadAllText(Path.Combine(Fixtures.SampleJob, "requirement.txt")));

        Assert.Equal(regex.Company, hermes.Company);
        Assert.Equal(regex.Kinds, hermes.Kinds);
        Assert.Equal(regex.BankLast4, hermes.BankLast4);
        Assert.Equal(regex.CardLast4, hermes.CardLast4);
    }

    [Theory]
    [InlineData("""["Deposit", "check", "CREDITCARD", "Check"]""")]
    [InlineData("""["CreditCard", "Deposit", "Check"]""")]
    public void Should_ExpandAndOrderKindsIgnoringCase_When_Mapping(string kinds)
    {
        var spec = Parse(Answer(kinds: kinds)).ToJobSpec();

        Assert.Equal([TxnKind.Check, TxnKind.CcCharge, TxnKind.CcCredit, TxnKind.Deposit], spec.Kinds);
    }

    [Theory]
    [InlineData("""["Wire"]""")]
    [InlineData("""["CcCharge"]""")]
    [InlineData("""["Skip"]""")]
    [InlineData("""[null]""")]
    public void Should_RejectKind_When_NotAKnownT1Kind(string kinds)
    {
        var errors = Parse(Answer(kinds: kinds)).Validate();

        Assert.Contains(errors, e => e.StartsWith("kinds contains", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("452")]
    [InlineData("45211")]
    [InlineData("45a1")]
    [InlineData(" 452")]
    [InlineData("")]
    [InlineData("٤٥٢١")]
    public void Should_RejectLast4_When_NotExactlyFourAsciiDigits(string value)
    {
        var json = JsonSerializer.Serialize(new[] { value });

        Assert.Contains(Parse(Answer(bank: json)).Validate(), e => e.StartsWith("bankLast4 contains", StringComparison.Ordinal));
        Assert.Contains(Parse(Answer(card: json)).Validate(), e => e.StartsWith("cardLast4 contains", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "bankLast4": [], "cardLast4": [] }""", "kinds is required")]
    [InlineData("""{ "kinds": [], "cardLast4": [] }""", "bankLast4 is required")]
    [InlineData("""{ "kinds": [], "bankLast4": [] }""", "cardLast4 is required")]
    public void Should_RequireArrays_When_FieldIsMissing(string json, string expected)
    {
        Assert.Contains(Parse(json).Validate(), e => e.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Should_AcceptEmptyKinds_When_NoSectionIsRecognised()
    {
        var answer = Parse("""{ "company": null, "kinds": [], "bankLast4": [], "cardLast4": [] }""");

        Assert.Empty(answer.Validate());
        Assert.Empty(answer.ToJobSpec().Kinds);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("\"Company\"")]
    [InlineData("\"company\"")]
    public void Should_DropCompany_When_MissingOrPlaceholder(string company)
    {
        Assert.Null(Parse(Answer(company: company)).ToJobSpec().Company);
    }

    [Fact]
    public void Should_TrimCompany_When_Mapping()
    {
        Assert.Equal("Acme LLC", Parse(Answer(company: "\" Acme LLC \"")).ToJobSpec().Company);
    }

    [Fact]
    public void Should_RemoveDuplicateLast4_When_Mapping()
    {
        var spec = Parse(Answer(bank: """["4521", "4521", "1234"]""")).ToJobSpec();

        Assert.Equal(["4521", "1234"], spec.BankLast4);
    }

    [Fact]
    public void Should_LookUpFieldRulesIgnoringCase_When_Mapping()
    {
        var spec = Parse(Answer()).ToJobSpec();

        Assert.Equal("ACH", spec.FieldRules["CARD"]["payee"]);
    }

    private static SpecAnswer Parse(string json) =>
        JsonSerializer.Deserialize<SpecAnswer>(json, JsonOptions.Default)!;

    private static string Answer(
        string company = "\"Acme\"", string kinds = """["Check"]""", string bank = """["4521"]""", string card = """["7788"]""") =>
        $$"""
        { "company": {{company}}, "kinds": {{kinds}}, "bankLast4": {{bank}}, "cardLast4": {{card}},
          "fieldRules": { "card": { "Payee": "ACH" } } }
        """;
}
