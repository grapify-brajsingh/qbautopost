using System.Text.Json;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.SavedRequests;

public class PayloadRulesV2Tests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Rows(string row0Extra = "", string row2Extra = "") =>
        $$"""
        { "reference": "r", "controlTotal": 3.00, "email": "a@x.com", "pushId": "p1", "transactions": [
          { "externalId": "a", "amount": 1.00{{row0Extra}} },
          { "externalId": "b", "amount": 1.00 },
          { "externalId": "c", "amount": 1.00{{row2Extra}} } ] }
        """;

    [Fact]
    public void Should_HashEqual_When_OnlyEmailOrPushIdDiffer()
    {
        var a = J("""{ "reference": "r", "email": "a@x.com", "pushId": "1", "transactions": [] }""");
        var b = J("""{ "reference": "r", "email": "b@x.com", "pushId": "2", "transactions": [] }""");

        Assert.Equal(PayloadCanonicalizer.Sha256(a, PayloadRules.V2), PayloadCanonicalizer.Sha256(b, PayloadRules.V2));
    }

    [Fact]
    public void Should_HashEqual_When_PostTrueVersusMissing()
    {
        var a = J(Rows(row2Extra: ", \"post\": true"));
        var b = J(Rows());

        Assert.Equal(PayloadCanonicalizer.Sha256(a, PayloadRules.V2), PayloadCanonicalizer.Sha256(b, PayloadRules.V2));
    }

    [Fact]
    public void Should_HashDiffer_When_PostFalseVersusMissing()
    {
        var a = J(Rows(row2Extra: ", \"post\": false"));
        var b = J(Rows());

        Assert.NotEqual(PayloadCanonicalizer.Sha256(a, PayloadRules.V2), PayloadCanonicalizer.Sha256(b, PayloadRules.V2));
    }

    [Fact]
    public void Should_KeepNestedPost_When_PostIsNotADirectRowKey()
    {
        var p = J("""{ "email": { "post": true }, "transactions": [ { "memo": { "post": true } } ] }""");

        var canonical = PayloadCanonicalizer.Canonicalize(p, PayloadRules.V2);

        Assert.Equal("""{"transactions":[{"memo":{"post":true}}]}""", canonical);
    }

    [Fact]
    public void Should_HashEqualToV1_When_PayloadHasNoV2Keys()
    {
        var p = J(Fixtures.Read("sync-post", "held.json"));

        Assert.Equal(PayloadCanonicalizer.Sha256(p), PayloadCanonicalizer.Sha256(p, PayloadRules.V2));
    }

    [Fact]
    public void Should_ReportPostOff_When_PostSetFalse()
    {
        var change = Assert.Single(PayloadDiff.Compute(J(Rows()), J(Rows(row2Extra: ", \"post\": false")), PayloadRules.V2));

        Assert.Equal("replace", change.Op);
        Assert.Equal("$.transactions[2].post", change.Path);
        Assert.Equal("true", change.Old!.ToJsonString());
        Assert.Equal("false", change.New!.ToJsonString());
    }

    [Fact]
    public void Should_ReportPostOn_When_PostKeyRemoved()
    {
        var change = Assert.Single(PayloadDiff.Compute(J(Rows(row2Extra: ", \"post\": false")), J(Rows()), PayloadRules.V2));

        Assert.Equal("replace", change.Op);
        Assert.Equal("$.transactions[2].post", change.Path);
        Assert.Equal("false", change.Old!.ToJsonString());
        Assert.Equal("true", change.New!.ToJsonString());
    }

    [Fact]
    public void Should_ReportNothing_When_PostTrueVersusMissingDiffed()
    {
        Assert.Empty(PayloadDiff.Compute(J(Rows()), J(Rows(row0Extra: ", \"post\": true")), PayloadRules.V2));
    }

    [Fact]
    public void Should_IgnoreEmailAndPushId_When_Diffed()
    {
        var a = J("""{ "reference": "r", "email": "a@x.com", "pushId": "1" }""");
        var b = J("""{ "reference": "r", "email": "b@x.com", "pushId": "2" }""");

        Assert.Empty(PayloadDiff.Compute(a, b, PayloadRules.V2));
    }

    [Fact]
    public void Should_KeepV1Behaviour_When_RulesOmitted()
    {
        var a = J("""{ "reference": "r", "pushId": "1" }""");
        var b = J("""{ "reference": "r", "pushId": "2" }""");

        var change = Assert.Single(PayloadDiff.Compute(a, b));

        Assert.Equal("$.pushId", change.Path);
        Assert.Equal("replace", change.Op);
    }

    [Fact]
    public void Should_ReportAddedRowAsStored_When_RowAddedWithoutPost()
    {
        var before = J("""{ "transactions": [ { "externalId": "a", "amount": 1.00 } ] }""");
        var after = J("""{ "transactions": [ { "externalId": "a", "amount": 1.00 }, { "externalId": "b", "amount": 2.00 } ] }""");

        var change = Assert.Single(PayloadDiff.Compute(before, after, PayloadRules.V2));

        Assert.Equal("add", change.Op);
        Assert.Equal("$.transactions[1]", change.Path);
        var added = Assert.IsType<System.Text.Json.Nodes.JsonObject>(change.New);
        Assert.False(added.ContainsKey("post"));
    }
}
