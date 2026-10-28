using QbAutopost.Core.Dashboard;
using Xunit;

namespace QbAutopost.Core.Tests.Dashboard;

public class PercentilesTests
{
    [Fact]
    public void Should_ReturnNearestRank_When_Odd()
    {
        var sorted = new[] { 10, 20, 30 };

        Assert.Equal(20, Percentiles.NearestRank(sorted, 50));
        Assert.Equal(30, Percentiles.NearestRank(sorted, 90));
    }

    [Fact]
    public void Should_ReturnNearestRank_When_Even()
    {
        Assert.Equal(2, Percentiles.NearestRank(new[] { 1, 2, 3, 4 }, 50));
    }

    [Fact]
    public void Should_Throw_When_ListIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => Percentiles.NearestRank(Array.Empty<int>(), 50));
    }
}
