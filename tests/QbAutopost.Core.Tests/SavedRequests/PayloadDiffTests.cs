using System.Text.Json;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.SavedRequests;

public class PayloadDiffTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (JsonElement Before, JsonElement After) Pair(string fixture)
    {
        var doc = JsonDocument.Parse(Fixtures.Read("saved-requests", fixture)).RootElement.Clone();
        return (doc.GetProperty("before"), doc.GetProperty("after"));
    }

    [Fact]
    public void Should_ReturnEmpty_When_PreviousIsNull()
    {
        Assert.Empty(PayloadDiff.Compute(null, J("""{ "reference": "r" }""")));
    }

    [Fact]
    public void Should_ReturnEmpty_When_PayloadsCanonicallyEqual()
    {
        var a = J("""{ "reference": "r", "controlTotal": 5.0 }""");
        var b = J("""{ "controlTotal": 5.00, "reference": "r" }""");

        Assert.Empty(PayloadDiff.Compute(a, b));
    }

    [Fact]
    public void Should_ReportReplace_When_PayeeChanged()
    {
        var (before, after) = Pair("diff-payee-edit.json");

        var change = Assert.Single(PayloadDiff.Compute(before, after));

        Assert.Equal("replace", change.Op);
        Assert.Equal("$.transactions[3].payee", change.Path);
        Assert.Equal("ABC PLUMBING", change.Old!.GetValue<string>());
        Assert.Equal("ABC Plumbing LLC", change.New!.GetValue<string>());
    }

    [Fact]
    public void Should_MatchRowsByExternalId_When_EveryRowHasOne()
    {
        var (before, after) = Pair("diff-delete-middle-row.json");

        var change = Assert.Single(PayloadDiff.Compute(before, after));

        Assert.Equal("remove", change.Op);
        Assert.Equal("$.transactions[1]", change.Path);
        Assert.Equal("x2", change.Old!["externalId"]!.GetValue<string>());
        Assert.Null(change.New);
    }

    [Fact]
    public void Should_MatchRowsByIndex_When_AnyRowLacksExternalId()
    {
        var (before, after) = Pair("diff-rows-without-external-id.json");

        var changes = PayloadDiff.Compute(before, after);

        // Index matching: row 0 is compared with row 0, so no whole-row add/remove appears.
        Assert.Contains(changes, c => c.Op == "replace" && c.Path == "$.transactions[0].payee");
        Assert.Contains(changes, c => c.Op == "replace" && c.Path == "$.transactions[1].payee");
        Assert.DoesNotContain(changes, c => c.Path is "$.transactions[0]" or "$.transactions[1]");
    }

    [Fact]
    public void Should_TreatAmountsAsDecimals_When_Comparing()
    {
        var a = J("""{ "transactions": [ { "externalId": "1", "amount": 184.3 } ] }""");
        var b = J("""{ "transactions": [ { "externalId": "1", "amount": 184.30 } ] }""");

        Assert.Empty(PayloadDiff.Compute(a, b));
    }

    [Fact]
    public void Should_ReportMoneyReplace_When_ControlTotalChanged()
    {
        var change = Assert.Single(PayloadDiff.Compute(J("""{ "controlTotal": 4820.17 }"""), J("""{ "controlTotal": 4784.17 }""")));

        Assert.Equal("$.controlTotal", change.Path);
        Assert.Equal("4820.17", change.Old!.ToJsonString());
        Assert.Equal("4784.17", change.New!.ToJsonString());
    }

    [Fact]
    public void Should_ReportAddAndRemove_When_TopLevelFieldAppearsOrDisappears()
    {
        var added = Assert.Single(PayloadDiff.Compute(J("{}"), J("""{ "reference": "r" }""")));
        var removed = Assert.Single(PayloadDiff.Compute(J("""{ "reference": "r" }"""), J("{}")));

        Assert.Equal(("add", "$.reference"), (added.Op, added.Path));
        Assert.Null(added.Old);
        Assert.Equal("r", added.New!.GetValue<string>());
        Assert.Equal(("remove", "$.reference"), (removed.Op, removed.Path));
        Assert.Equal("r", removed.Old!.GetValue<string>());
        Assert.Null(removed.New);
    }

    [Fact]
    public void Should_ReportAddAtNewIndex_When_RowAppended()
    {
        var a = J("""{ "transactions": [ { "externalId": "1", "amount": 1 } ] }""");
        var b = J("""{ "transactions": [ { "externalId": "1", "amount": 1 }, { "externalId": "2", "amount": 2 } ] }""");

        var change = Assert.Single(PayloadDiff.Compute(a, b));

        Assert.Equal(("add", "$.transactions[1]"), (change.Op, change.Path));
    }
}
