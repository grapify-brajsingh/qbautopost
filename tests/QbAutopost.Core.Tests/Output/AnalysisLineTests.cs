using System.Text.Json;
using QbAutopost.Core.Models;
using QbAutopost.Core.Output;
using QbAutopost.Core.Tests.TestSupport;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Tests.Output;

/// <summary>One <c>analysis.json</c> line (spec §10): every field, and the tier 3–4 evidence.</summary>
public sealed class AnalysisLineTests
{
    private static readonly string[] SpecFields =
    [
        "requestId", "file", "lineNo", "date", "description", "amount", "direction", "kind", "account", "payee",
        "lineAccount", "refNumber", "tier", "confidence", "reason", "candidates", "invoiceRef", "decision",
    ];

    private static MappedTxn HeldByG3() => new()
    {
        Line = Lines.Bank(Direction.Debit, 3199.70m, "ACH UNKNOWN PLUMBER"),
        Kind = TxnKind.Check,
        Decision = Decision.Hold,
        Confidence = Confidence.Hold,
        Reason = HoldReasons.LowConfidence,
        Account = "Chase Checking",
        Payee = "Joe's Plumbing",
        RefNumber = "ACH",
        Tier = 4,
        ModelConfidence = 0.6,
        Candidates = ["Repairs and Maintenance", "Office Supplies"],
        InvoiceRef = "joe-77.pdf",
    };

    private static JsonElement Serialise(MappedTxn txn) =>
        JsonSerializer.SerializeToElement(AnalysisLine.From(txn), JsonOptions.Default);

    [Fact]
    public void Should_WriteEverySpecField_When_LineIsSerialised()
    {
        var json = Serialise(HeldByG3());

        Assert.All(SpecFields, f => Assert.True(json.TryGetProperty(f, out _), f));
    }

    [Fact]
    public void Should_WriteTierScoreAndAccountCandidates_When_G3HeldTheLine()
    {
        var json = Serialise(HeldByG3());

        Assert.Equal(4, json.GetProperty("tier").GetInt32());
        Assert.Equal("hold", json.GetProperty("confidence").GetString());
        Assert.Equal(0.6, json.GetProperty("modelConfidence").GetDouble());
        Assert.Equal("low-confidence", json.GetProperty("reason").GetString());
        Assert.Equal("hold", json.GetProperty("decision").GetString());
        Assert.Equal(
            ["Repairs and Maintenance", "Office Supplies"],
            json.GetProperty("candidates").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("lineAccount").ValueKind);
    }

    [Fact]
    public void Should_WriteModelScore_When_LinePostsAtInvoiceTier()
    {
        var json = Serialise(HeldByG3() with
        {
            Decision = Decision.Post,
            Confidence = Confidence.Invoice,
            Reason = null,
            Tier = 3,
            ModelConfidence = 0.82,
            LineAccount = "Repairs and Maintenance",
        });

        Assert.Equal(3, json.GetProperty("tier").GetInt32());
        Assert.Equal("invoice", json.GetProperty("confidence").GetString());
        Assert.Equal(0.82, json.GetProperty("modelConfidence").GetDouble());
        Assert.Equal("post", json.GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("reason").ValueKind);
    }

    [Fact]
    public void Should_WriteNullModelScore_When_ModelWasNotAsked()
    {
        var json = Serialise(HeldByG3() with { Tier = null, ModelConfidence = null, Reason = HoldReasons.NoAccounts });

        Assert.Equal(JsonValueKind.Null, json.GetProperty("modelConfidence").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("tier").ValueKind);
    }

    [Fact]
    public void Should_RoundTripModelScore_When_AnalysisLineIsReadBack()
    {
        var text = JsonSerializer.Serialize(AnalysisLine.From(HeldByG3()), JsonOptions.Default);

        var back = JsonSerializer.Deserialize<AnalysisLine>(text, JsonOptions.Default)!;

        Assert.Equal(0.6, back.ModelConfidence);
        Assert.Equal(4, back.Tier);
        Assert.Equal(Decision.Hold, back.Decision);
    }
}
