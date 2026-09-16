using QbAutopost.Core.Text;

namespace QbAutopost.Core.Tests.Text;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("184.32", 184.32)]
    [InlineData("-184.32", -184.32)]
    [InlineData("$1,250.00", 1250.00)]
    [InlineData("(42.10)", -42.10)]
    [InlineData("42.10-", -42.10)]
    public void Should_Parse_When_CellIsAStatementAmount(string text, double expected)
    {
        Assert.True(Money.TryParse(text, out var amount));
        Assert.Equal((decimal)expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("twelve")]
    [InlineData("1.005")]
    public void Should_Reject_When_CellIsNotAnExactAmount(string text)
    {
        Assert.False(Money.TryParse(text, out _));
    }

    [Fact]
    public void Should_FormatTwoDecimalsInvariant_When_Writing()
    {
        Assert.Equal("1250.50", Money.Format(1250.5m));
    }
}
