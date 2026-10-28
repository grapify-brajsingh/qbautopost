using System.Text.Json.Nodes;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.Tests.TestSupport;
using Xunit;

namespace QbAutopost.Core.Tests.Extraction;

public class ExtractionTidyTests
{
    private static JsonObject TidyRows(string rowsJson, string extra = "") =>
        ExtractionTidy.Tidy(ExtractionAnswerTests.Parse(
            "{ \"is_bank_statement\": true, " + extra + " \"transactions\": [ " + rowsJson + " ] }"));

    private static JsonObject Row(JsonObject payload, int index) => payload["transactions"]!.AsArray()[index]!.AsObject();

    private static JsonObject TidyMessy() =>
        ExtractionTidy.Tidy(ExtractionAnswerTests.Parse(Fixtures.Read("extractions", "messy-rows.reply.json")));

    [Fact]
    public void Should_LeaveFieldOut_When_ModelOmitted()
    {
        var row = Row(TidyRows("""{ "kind": "Check", "amount": 1 }"""), 0);

        Assert.False(row.ContainsKey("account"));
    }

    [Fact]
    public void Should_LeaveFieldOut_When_ModelReturnedBlank()
    {
        var row = Row(TidyRows("""{ "kind": "Check", "amount": 1, "payee": "  ", "memo": "" }"""), 0);

        Assert.False(row.ContainsKey("payee"));
        Assert.False(row.ContainsKey("memo"));
    }

    [Fact]
    public void Should_CopyTrimmedText_When_ModelReturnedValue()
    {
        var row = Row(TidyRows("""{ "kind": "Check", "amount": 1, "lineAccount": " Supplies ", "date": "08/05/2026" }"""), 0);

        Assert.Equal("Supplies", (string?)row["lineAccount"]);
        Assert.Equal("08/05/2026", (string?)row["date"]);
    }

    [Fact]
    public void Should_NeverWriteN8nDefaults_When_FieldsMissing()
    {
        var text = TidyMessy().ToJsonString();

        foreach (var banned in new[] { "Capital One - 3188", "6891", "Uncategorized", "Ask My Accountant", "zemen-2019-12-p01" })
        {
            Assert.DoesNotContain(banned, text);
        }
    }

    [Theory]
    [InlineData("Deposit")]
    [InlineData("deposit")]
    [InlineData("DEPOSIT")]
    public void Should_SetDeposit_When_KindIsDepositAnyCase(string kind)
    {
        var row = Row(TidyRows("{ \"kind\": \"" + kind + "\", \"amount\": 1 }"), 0);

        Assert.Equal("Deposit", (string?)row["kind"]);
    }

    [Theory]
    [InlineData("Check")]
    [InlineData("Whatever")]
    [InlineData("")]
    public void Should_SetCheck_When_KindIsAnythingElse(string kind)
    {
        var row = Row(TidyRows("{ \"kind\": \"" + kind + "\", \"amount\": 1 }"), 0);

        Assert.Equal("Check", (string?)row["kind"]);
    }

    [Fact]
    public void Should_SetCheck_When_KindMissing()
    {
        Assert.Equal("Check", (string?)Row(TidyRows("""{ "amount": 1 }"""), 0)["kind"]);
    }

    [Fact]
    public void Should_SetCheck_When_KindCheckAndAmountPositive()
    {
        var row = Row(TidyRows("""{ "kind": "Check", "amount": 318.40 }"""), 0);

        Assert.Equal("Check", (string?)row["kind"]);
    }

    [Fact]
    public void Should_MakeAmountPositive_When_Negative()
    {
        var row = Row(TidyRows("""{ "kind": "Check", "amount": -60.00 }"""), 0);

        Assert.Equal(60.00m, (decimal)row["amount"]!);
    }

    [Fact]
    public void Should_OmitAmount_When_NotANumber()
    {
        var payload = TidyRows("""{ "kind": "Check", "amount": "1,240.00", "memo": "kept" }""");

        Assert.Single(payload["transactions"]!.AsArray());
        Assert.False(Row(payload, 0).ContainsKey("amount"));
        Assert.Equal("kept", (string?)Row(payload, 0)["memo"]);
    }

    [Fact]
    public void Should_OmitAmount_When_Missing()
    {
        Assert.False(Row(TidyRows("""{ "kind": "Check" }"""), 0).ContainsKey("amount"));
    }

    [Fact]
    public void Should_KeepAmountDigits_When_Decimal()
    {
        var payload = TidyRows("""{ "kind": "Deposit", "amount": 1240.10 }""");

        Assert.Contains("\"amount\":1240.10", payload.ToJsonString());
    }

    [Fact]
    public void Should_RenumberExternalId_When_ModelGaveOwn()
    {
        var payload = TidyRows("""{ "externalId": "row-9", "amount": 1 }, { "externalId": "x", "amount": 2 }""");

        Assert.Equal("row-1", (string?)Row(payload, 0)["externalId"]);
        Assert.Equal("row-2", (string?)Row(payload, 1)["externalId"]);
    }

    [Fact]
    public void Should_SetControlTotalNull_When_Tidied()
    {
        var payload = TidyMessy();

        Assert.True(payload.ContainsKey("controlTotal"));
        Assert.Null(payload["controlTotal"]);
    }

    [Fact]
    public void Should_SetReferenceNull_When_Blank()
    {
        var payload = TidyMessy();

        Assert.True(payload.ContainsKey("reference"));
        Assert.Null(payload["reference"]);
    }

    [Fact]
    public void Should_TrimReference_When_Present()
    {
        var payload = TidyRows("""{ "amount": 1 }""", "\"reference\": \" ST-1 \",");

        Assert.Equal("ST-1", (string?)payload["reference"]);
    }

    [Fact]
    public void Should_DropRefNumber_When_Present()
    {
        var row = Row(TidyMessy(), 0);

        Assert.False(row.ContainsKey("refNumber"));
    }

    [Fact]
    public void Should_OmitCheckNo_When_BlankOrNull()
    {
        var payload = TidyMessy();

        Assert.False(Row(payload, 1).ContainsKey("checkNo"));
        Assert.False(Row(payload, 4).ContainsKey("checkNo"));
    }

    [Fact]
    public void Should_KeepEveryRow_When_Tidied()
    {
        Assert.Equal(5, TidyMessy()["transactions"]!.AsArray().Count);
    }

    [Fact]
    public void Should_ReturnEmptyTransactions_When_NotAStatement()
    {
        var payload = ExtractionTidy.Tidy(ExtractionAnswerTests.Parse(
            Fixtures.Read("extractions", "not-a-statement.reply.json")));

        Assert.Empty(payload["transactions"]!.AsArray());
    }
}
