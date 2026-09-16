using System.Text.Json;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Tests.Text;

public sealed class JsonOptionsTests
{
    [Fact]
    public void Should_WriteEnumsInCamelCase_When_Serialising()
    {
        Assert.Equal("\"ready\"", JsonSerializer.Serialize(JobStatus.Ready, JsonOptions.Default));
        Assert.Equal("\"ccCharge\"", JsonSerializer.Serialize(TxnKind.CcCharge, JsonOptions.Default));
    }

    [Theory]
    [InlineData("\"Bank\"")]
    [InlineData("\"bank\"")]
    [InlineData("\"BANK\"")]
    public void Should_ReadEnumsIgnoringCase_When_Deserialising(string json)
    {
        Assert.Equal(SourceKind.Bank, JsonSerializer.Deserialize<SourceKind>(json, JsonOptions.Default));
    }
}
