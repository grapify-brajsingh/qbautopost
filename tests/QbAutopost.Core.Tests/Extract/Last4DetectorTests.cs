using QbAutopost.Core.Extract;

namespace QbAutopost.Core.Tests.Extract;

public sealed class Last4DetectorTests
{
    [Theory]
    [InlineData("chase-checking-4521.pdf", "4521")]
    [InlineData("card_7788.csv", "7788")]
    [InlineData("acct4521.xlsx", "4521")]
    public void Should_FindLastFour_When_FileNameHasOneFourDigitGroup(string fileName, string expected)
    {
        Assert.Equal(expected, Last4Detector.FromFileName(fileName));
    }

    [Theory]
    [InlineData("statement.csv")]
    [InlineData("acct-123456.csv")]
    [InlineData("2026-chase-4521.csv")]
    public void Should_ReturnNull_When_FileNameHasNoSingleFourDigitGroup(string fileName)
    {
        Assert.Null(Last4Detector.FromFileName(fileName));
    }

    [Theory]
    [InlineData("XXXX9012", "9012")]
    [InlineData("...4521", "4521")]
    [InlineData("12", null)]
    public void Should_TakeLastFourDigits_When_ReadingAnAccountCell(string cell, string? expected)
    {
        Assert.Equal(expected, Last4Detector.FromCell(cell));
    }
}
