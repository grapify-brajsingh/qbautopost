using System.Text.Json;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Tests.SavedRequests;

/// <summary>T-1104 / dashboard v2 spec 7.1: externalId assignment.</summary>
public sealed class ExternalIdAssignerTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string?[] Ids(JsonElement payload) =>
        payload.GetProperty("transactions").EnumerateArray()
            .Select(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("externalId", out var e)
                ? e.GetString() : null)
            .ToArray();

    [Fact]
    public void Should_AssignRowN_When_ExternalIdMissing()
    {
        var result = ExternalIdAssigner.AssignOnSave(Parse(
            """{"transactions":[{"amount":1},{"externalId":"mine"},{"externalId":null},{"externalId":""}]}"""));

        Assert.Equal(new[] { "row-1", "mine", "row-3", "row-4" }, Ids(result));
    }

    [Fact]
    public void Should_SuffixId_When_RowNAlreadyUsed()
    {
        var result = ExternalIdAssigner.AssignOnSave(Parse(
            """{"transactions":[{"amount":1},{"externalId":"row-1"}]}"""));

        Assert.Equal(new[] { "row-1-2", "row-1" }, Ids(result));
    }

    [Fact]
    public void Should_KeepSuffixing_When_SuffixedIdAlsoUsed()
    {
        var result = ExternalIdAssigner.AssignOnSave(Parse(
            """{"transactions":[{},{"externalId":"row-1"},{"externalId":"row-1-2"}]}"""));

        Assert.Equal(new[] { "row-1-3", "row-1", "row-1-2" }, Ids(result));
    }

    [Fact]
    public void Should_AssignEditIds_When_EditRowLacksId()
    {
        var result = ExternalIdAssigner.AssignOnEdit(Parse(
            """{"transactions":[{"externalId":"dash-1"},{"amount":2}]}"""), 4);

        Assert.Equal(new[] { "dash-1", "edit4-2" }, Ids(result));
    }

    [Fact]
    public void Should_SuffixEditId_When_EditIdTaken()
    {
        var result = ExternalIdAssigner.AssignOnEdit(Parse(
            """{"transactions":[{},{"externalId":"edit4-1"}]}"""), 4);

        Assert.Equal(new[] { "edit4-1-2", "edit4-1" }, Ids(result));
    }

    [Fact]
    public void Should_LeaveDuplicateCallerIds_When_Assigning()
    {
        var result = ExternalIdAssigner.AssignOnSave(Parse(
            """{"transactions":[{"externalId":"a"},{"externalId":"a"},{}]}"""));

        Assert.Equal(new[] { "a", "a", "row-3" }, Ids(result));
    }

    [Fact]
    public void Should_SkipNonObjectRows_When_Assigning()
    {
        var result = ExternalIdAssigner.AssignOnSave(Parse("""{"transactions":[5,{},"x",null]}"""));

        Assert.Equal(JsonValueKind.Number, result.GetProperty("transactions")[0].ValueKind);
        Assert.Equal("row-2", result.GetProperty("transactions")[1].GetProperty("externalId").GetString());
        Assert.Equal(JsonValueKind.String, result.GetProperty("transactions")[2].ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("transactions")[3].ValueKind);
    }

    [Fact]
    public void Should_ReturnUnchanged_When_TransactionsNotArray()
    {
        var input = Parse("""{"transactions":{"a":1}}""");

        Assert.Equal(input.GetRawText(), ExternalIdAssigner.AssignOnSave(input).GetRawText());
        Assert.Equal("[1]", ExternalIdAssigner.AssignOnSave(Parse("[1]")).GetRawText());
    }

    [Fact]
    public void Should_KeepAmountsExact_When_Assigning()
    {
        var result = ExternalIdAssigner.AssignOnSave(Parse("""{"controlTotal":12.50,"transactions":[{"amount":12.50}]}"""));

        Assert.Equal(12.50m, result.GetProperty("controlTotal").GetDecimal());
        Assert.Equal(12.50m, result.GetProperty("transactions")[0].GetProperty("amount").GetDecimal());
    }
}
