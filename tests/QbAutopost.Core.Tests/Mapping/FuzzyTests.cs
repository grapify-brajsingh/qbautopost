using QbAutopost.Core.Mapping;

namespace QbAutopost.Core.Tests.Mapping;

public sealed class FuzzyTests
{
    [Fact]
    public void Should_ScoreOne_When_NameAppearsAsTokensInDescription()
    {
        Assert.Equal(1.0, Fuzzy.MatchScore("HOME DEPOT #4521 NOIDA", "Home Depot"));
    }

    [Fact]
    public void Should_ScoreHigh_When_NameDiffersByPunctuation()
    {
        Assert.True(Fuzzy.MatchScore("ACH DEBIT JOES PLUMBING", "Joe's Plumbing") >= 0.85);
    }

    [Fact]
    public void Should_ScoreLow_When_NameIsUnrelated()
    {
        Assert.True(Fuzzy.MatchScore("ACH DEBIT UNKNOWN PLUMBER SVC", "Amazon") < 0.5);
    }

    [Fact]
    public void Should_RankBestFirst_When_SeveralNamesGiven()
    {
        var ranked = Fuzzy.Rank("HOME DEPOT #4521", ["Amazon", "Home Depot", "Home Goods"], 2);

        Assert.Equal(["Home Depot", "Home Goods"], ranked.Select(h => h.Name));
    }
}
