using System.Text.Json;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Tests.SavedRequests;

/// <summary>T-1102 / dashboard v2 §6.1: the per-row <c>post</c> flag is read from the stored payload.</summary>
public sealed class NotPostedRowsTests
{
    private static NotPostedRead Read(string json) => NotPostedRows.Read(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Should_ReturnIndexes_When_PostIsFalse()
    {
        var read = Read("""{"transactions":[{"post":true},{},{"post":false}]}""");

        Assert.Equal(new[] { 2 }, read.Indexes);
        Assert.Empty(read.Errors);
    }

    [Fact]
    public void Should_TreatMissingOrTrueAsPost_When_Read()
    {
        var read = Read("""{"transactions":[{"post":true},{"amount":1}]}""");

        Assert.Empty(read.Indexes);
        Assert.Empty(read.Errors);
    }

    [Fact]
    public void Should_ReportPathError_When_PostIsNotBoolean()
    {
        var read = Read("""{"transactions":[{},{},{},{"post":"no"}]}""");

        Assert.Equal("$.transactions[3].post: must be true or false", Assert.Single(read.Errors));
        Assert.Empty(read.Indexes);
    }

    [Fact]
    public void Should_ReturnEmpty_When_TransactionsIsNotArray()
    {
        var read = Read("""{"transactions":"x"}""");

        Assert.Empty(read.Indexes);
        Assert.Empty(read.Errors);
    }
}
