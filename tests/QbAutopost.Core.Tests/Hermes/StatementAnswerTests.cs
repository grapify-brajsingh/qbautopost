using System.Text.Json.Nodes;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Hermes;

/// <summary>T2 answer validation (spec §9.2): amounts &gt; 0, ISO dates, known kind and direction.</summary>
public sealed class StatementAnswerTests
{
    [Fact]
    public void Should_AcceptFixtureAnswer_When_ItMatchesTheSchema()
    {
        var answer = Parse(Fixtures.Read("hermes", "statement.json"), out var errors);

        Assert.Empty(errors);
        Assert.Equal(7, answer!.Rows!.Count);
    }

    [Theory]
    [InlineData(-184.32)]
    [InlineData(0)]
    public void Should_RejectAmount_When_ItIsNotPositive(double amount)
    {
        var errors = Errors(a => a["rows"]![0]!["amount"] = (decimal)amount);

        Assert.Contains(errors, e => e.Contains("rows[0].amount", StringComparison.Ordinal) && e.Contains("greater than 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectAmount_When_ItHasMoreThanTwoDecimals()
    {
        var errors = Errors(a => a["rows"]![1]!["amount"] = 420.005m);

        Assert.Contains(errors, e => e.Contains("rows[1].amount", StringComparison.Ordinal) && e.Contains("2 decimal", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectAmount_When_ItIsMissing()
    {
        var errors = Errors(a => a["rows"]![0]!.AsObject().Remove("amount"));

        Assert.Contains(errors, e => e.Contains("rows[0].amount", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectAmount_When_ItIsAString()
    {
        Parse(Mutate(a => a["rows"]![0]!["amount"] = "1500.00"), out var errors);

        Assert.Contains(errors, e => e.Contains("not valid JSON for the expected shape", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("08/22/2026")]
    [InlineData("2026-8-22")]
    [InlineData("2026-02-30")]
    [InlineData("")]
    public void Should_RejectRowDate_When_ItIsNotAnIsoDate(string date)
    {
        var errors = Errors(a => a["rows"]![2]!["date"] = date);

        Assert.Contains(errors, e => e.Contains("rows[2].date", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectPeriod_When_StartIsAfterEnd()
    {
        var errors = Errors(a => a["periodStart"] = "2026-09-01");

        Assert.Contains(errors, e => e.Contains("periodStart", StringComparison.Ordinal) && e.Contains("after periodEnd", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectPeriodEnd_When_ItIsNotAnIsoDate()
    {
        var errors = Errors(a => a["periodEnd"] = "August 31, 2026");

        Assert.Contains(errors, e => e.Contains("periodEnd", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_AcceptNullSummaryFields_When_ThePartDoesNotShowThem()
    {
        var errors = Errors(a =>
        {
            foreach (var field in new[] { "accountLast4", "periodStart", "periodEnd", "openingBalance", "closingBalance", "transactionCount" })
            {
                a[field] = null;
            }
        });

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("withdrawal")]
    [InlineData("")]
    public void Should_RejectDirection_When_ItIsNotDebitOrCredit(string direction)
    {
        var errors = Errors(a => a["rows"]![3]!["direction"] = direction);

        Assert.Contains(errors, e => e.Contains("rows[3].direction", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_AcceptDirectionAndKind_When_CaseDiffers()
    {
        var errors = Errors(a =>
        {
            a["kind"] = "Card";
            a["rows"]![0]!["direction"] = "DEBIT";
        });

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("savings")]
    [InlineData(null)]
    public void Should_RejectKind_When_ItIsNotBankOrCard(string? kind)
    {
        var errors = Errors(a => a["kind"] = kind);

        Assert.Contains(errors, e => e.StartsWith("kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectAnswer_When_RowsAreMissing()
    {
        var errors = Errors(a => a.Remove("rows"));

        Assert.Contains(errors, e => e.StartsWith("rows is required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("45210")]
    [InlineData("45 1")]
    [InlineData("")]
    public void Should_RejectAccountLast4_When_ItIsNotFourDigits(string last4)
    {
        var errors = Errors(a => a["accountLast4"] = last4);

        Assert.Contains(errors, e => e.Contains("accountLast4", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectTransactionCount_When_ItIsNegative()
    {
        var errors = Errors(a => a["transactionCount"] = -1);

        Assert.Contains(errors, e => e.Contains("transactionCount", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectBalances_When_TheyHaveMoreThanTwoDecimals()
    {
        var errors = Errors(a =>
        {
            a["openingBalance"] = 13195.875m;
            a["rows"]![4]!["balance"] = 10164.771m;
        });

        Assert.Contains(errors, e => e.Contains("openingBalance", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("rows[4].balance", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectRow_When_DescriptionIsBlank()
    {
        var errors = Errors(a => a["rows"]![5]!["description"] = "  ");

        Assert.Contains(errors, e => e.Contains("rows[5].description", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectRow_When_ItIsNull()
    {
        var errors = Errors(a => a["rows"]![6] = null);

        Assert.Contains(errors, e => e.Contains("rows[6]", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectCheckNumber_When_ItIsANumber()
    {
        Parse(Mutate(a => a["rows"]![1]!["checkNo"] = 1043), out var errors);

        Assert.NotEmpty(errors);
    }

    private static IReadOnlyList<string> Errors(Action<JsonObject> mutate)
    {
        Parse(Mutate(mutate), out var errors);
        return errors;
    }

    private static string Mutate(Action<JsonObject> mutate)
    {
        var answer = JsonNode.Parse(Fixtures.Read("hermes", "statement.json"))!.AsObject();
        mutate(answer);
        return answer.ToJsonString();
    }

    private static StatementAnswer? Parse(string json, out IReadOnlyList<string> errors) =>
        JsonReply.TryParse<StatementAnswer>(json, out errors);
}
