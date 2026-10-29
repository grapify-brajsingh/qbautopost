using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Extraction;
using Xunit;

namespace QbAutopost.Core.Tests.Extraction;

public class UploadVersionBuilderTests
{
    private static JsonObject Extraction() => JsonNode.Parse("""
        { "transactions": [ { "id": "row-1", "kind": "Check", "amount": 5.00 } ], "controlTotal": null }
        """)!.AsObject();

    private static JsonObject Build(string body, JsonObject? extraction = null)
    {
        using var doc = JsonDocument.Parse(body);
        return UploadVersionBuilder.Version1(extraction ?? Extraction(), doc.RootElement.Clone());
    }

    [Fact]
    public void Should_TakeBodyReferenceAndTotal_When_BuildingVersion1()
    {
        var v = Build("""{ "reference": "INV-1", "controlTotal": 123.45 }""");

        Assert.Equal("INV-1", (string?)v["reference"]);
        Assert.Equal(123.45m, (decimal?)v["controlTotal"]);
        Assert.Equal("row-1", (string?)v["transactions"]![0]!["id"]);
    }

    [Fact]
    public void Should_KeepNullControlTotal_When_BodyNull()
    {
        var v = Build("""{ "reference": null, "controlTotal": null }""");

        Assert.True(v.ContainsKey("controlTotal"));
        Assert.Null(v["controlTotal"]);
        Assert.Null(v["reference"]);
    }

    [Fact]
    public void Should_IgnoreBodyTransactions_When_BuildingVersion1()
    {
        var v = Build("""{ "transactions": [ { "id": "evil" } ] }""");

        Assert.Single(v["transactions"]!.AsArray());
        Assert.Equal("row-1", (string?)v["transactions"]![0]!["id"]);
    }

    [Fact]
    public void Should_NotMutateExtraction_When_BuildingVersion1()
    {
        var ext = Extraction();
        var before = ext.ToJsonString();

        var v = Build("""{ "reference": "A" }""", ext);
        v["transactions"]![0]!["id"] = "changed";

        Assert.Equal(before, ext.ToJsonString());
    }
}
