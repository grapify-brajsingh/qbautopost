using System.Text.Json;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.Tests.TestSupport;
using QbAutopost.Core.Text;
using Xunit;

namespace QbAutopost.Core.Tests.Extraction;

public class ExtractionAnswerTests
{
    public static ExtractionAnswer Parse(string json) =>
        JsonSerializer.Deserialize<ExtractionAnswer>(json, JsonOptions.Default)!;

    [Fact]
    public void Should_FailValidation_When_IsBankStatementMissing()
    {
        var answer = Parse("""{ "transactions": [] }""");

        Assert.Contains(answer.Validate(), e => e.Contains("is_bank_statement"));
    }

    [Fact]
    public void Should_FailValidation_When_TransactionsMissing()
    {
        var answer = Parse("""{ "is_bank_statement": true }""");

        Assert.Contains(answer.Validate(), e => e.Contains("transactions"));
    }

    [Fact]
    public void Should_FailValidation_When_ItemIsNotAnObject()
    {
        var answer = Parse("""{ "is_bank_statement": true, "transactions": [ {}, 5, null ] }""");

        var errors = answer.Validate();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("transactions[1]"));
        Assert.Contains(errors, e => e.Contains("transactions[2]"));
    }

    [Fact]
    public void Should_PassValidation_When_NotAStatementWithNoRows()
    {
        var answer = Parse(Fixtures.Read("extractions", "not-a-statement.reply.json"));

        Assert.Empty(answer.Validate());
        Assert.False(answer.IsBankStatement);
    }

    [Fact]
    public void Should_PassValidation_When_ReplyFixtureIsWellFormed()
    {
        var answer = Parse(Fixtures.Read("extractions", "chase-checking-4521.reply.json"));

        Assert.Empty(answer.Validate());
        Assert.True(answer.IsBankStatement);
        Assert.Equal(4, answer.Transactions!.Count);
    }
}
