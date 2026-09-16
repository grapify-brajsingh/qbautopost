using QbAutopost.Core.Hermes;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Hermes;

/// <summary>Hermes T4 answer validation (spec §9.4): account from the list (case-sensitive exact), 0 ≤ confidence ≤ 1.</summary>
public sealed class AccountAnswerTests
{
    private static readonly IReadOnlyList<string> Accounts = ["Repairs and Maintenance", "Office Supplies", "Utilities"];

    [Fact]
    public void Should_Accept_When_AnswerIsTheFixture()
    {
        var answer = Parse(Fixtures.Read("hermes", "account.json"), out var errors);

        Assert.Empty(errors);
        Assert.Equal("Repairs and Maintenance", answer!.Account);
        Assert.Equal(0.82, answer.Confidence);
        Assert.Equal(["Office Supplies", "Utilities"], answer.Alternatives);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("1.0")]
    public void Should_Accept_When_ConfidenceIsOnABound(string confidence)
    {
        Parse(Answer("Utilities", confidence), out var errors);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    [InlineData("82")]
    public void Should_Reject_When_ConfidenceIsOutsideZeroToOne(string confidence)
    {
        Parse(Answer("Utilities", confidence), out var errors);

        Assert.Contains(errors, e => e.Contains("confidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_Reject_When_ConfidenceIsMissing()
    {
        Parse("""{ "account": "Utilities" }""", out var errors);

        Assert.Contains(errors, e => e.Contains("confidence is missing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    public void Should_Reject_When_AccountIsMissingOrBlank(string account)
    {
        Parse($$"""{ "account": {{account}}, "confidence": 0.9 }""", out var errors);

        Assert.Contains(errors, e => e.Contains("account is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_Reject_When_AccountIsNotInTheList()
    {
        Parse(Answer("Repairs", "0.9"), out var errors);

        var error = Assert.Single(errors);
        Assert.Contains("\"Repairs\"", error, StringComparison.Ordinal);
        Assert.Contains("exactly", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("repairs and maintenance")]
    [InlineData("Repairs and Maintenance ")]
    [InlineData("Repairs  and Maintenance")]
    public void Should_Reject_When_AccountOnlyResemblesAListedName(string account)
    {
        Parse(Answer(account, "0.9"), out var errors);

        Assert.Single(errors);
    }

    [Fact]
    public void Should_NotCheckTheList_When_AnswerIsAlreadyInvalid()
    {
        Parse(Answer("Nowhere", "2"), out var errors);

        var error = Assert.Single(errors);
        Assert.Contains("confidence", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Accept_When_AlternativesAreMissingOrNotListed()
    {
        Parse("""{ "account": "Utilities", "confidence": 0.5, "alternatives": ["Travel", null, ""] }""", out var errors);
        Parse("""{ "account": "Utilities", "confidence": 0.5 }""", out var noAlternatives);

        Assert.Empty(errors);
        Assert.Empty(noAlternatives);
    }

    [Fact]
    public void Should_SkipTheListCheck_When_NoCheckIsGiven()
    {
        var answer = JsonReply.TryParse<AccountAnswer>(Answer("Anything", "0.9"), out var errors);

        Assert.NotNull(answer);
        Assert.Empty(errors);
    }

    private static string Answer(string account, string confidence) =>
        $$"""{ "account": "{{account}}", "confidence": {{confidence}}, "reason": "r", "alternatives": [] }""";

    private static AccountAnswer? Parse(string json, out IReadOnlyList<string> errors) =>
        JsonReply.TryParse<AccountAnswer>(json, AccountAnswer.MustBeOneOf(Accounts), out errors);
}
