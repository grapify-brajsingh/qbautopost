using QbAutopost.Core.Companies;

namespace QbAutopost.Core.Tests.Companies;

/// <summary>M17 spec §4.5: exact match after normalising, no fuzzy distance.</summary>
public sealed class CompanyNameMatcherTests
{
    private static CompanyNames Co(int id, bool matchable, params string?[] names) => new(id, matchable, names);

    private static readonly CompanyNames Takoma = Co(1, true, "Takoma Park Grocery Store Inc", "Zemen Market");
    private static readonly CompanyNames SAndA = Co(2, true, "S & A Convenience Market LLC");

    [Theory]
    [InlineData("S & A CONVENIENCE MARKET LLC", "SACONVENIENCEMARKET")]
    [InlineData("SA CONVENIENCE MARKET", "SACONVENIENCEMARKET")]
    [InlineData("TAKOMA PARK GROCERY STORE INC", "TAKOMAPARKGROCERYSTORE")]
    [InlineData("Takoma Park Grocery Store Inc", "TAKOMAPARKGROCERYSTORE")]
    [InlineData("DBA ZEMEN MARKET", "ZEMENMARKET")]
    [InlineData("The Acme Co., L.L.C.", "ACME")]
    [InlineData("C & L Market", "CLMARKET")]
    [InlineData("Acme Corporation Ltd", "ACME")]
    public void Should_NormaliseToJoinedTokens_When_NameHasPunctuationAndSuffixes(string name, string expected) =>
        Assert.Equal(expected, CompanyNameMatcher.Normalize(name));

    [Fact]
    public void Should_MatchTakoma_When_HeaderPrintsTakomaParkGroceryStoreInc()
    {
        var m = CompanyNameMatcher.Match("TAKOMA PARK GROCERY STORE INC", "DBA ZEMEN MARKET", [Takoma, SAndA]);
        Assert.Equal(CompanyMatchKind.Matched, m.Match);
        Assert.Equal(1, m.CompanyId);
        Assert.Equal([1], m.Candidates);
    }

    [Fact]
    public void Should_MatchSAndA_When_HeaderPrintsSaConvenienceMarket()
    {
        var m = CompanyNameMatcher.Match("SA CONVENIENCE MARKET", null, [Takoma, SAndA]);
        Assert.Equal(CompanyMatchKind.Matched, m.Match);
        Assert.Equal(2, m.CompanyId);
    }

    [Fact]
    public void Should_MatchOnDbaAlias_When_AliasIsAMatchName()
    {
        var m = CompanyNameMatcher.Match(null, "DBA ZEMEN MARKET", [Takoma, SAndA]);
        Assert.Equal(CompanyMatchKind.Matched, m.Match);
        Assert.Equal(1, m.CompanyId);
    }

    [Fact]
    public void Should_ReturnNone_When_NameIsUnknown()
    {
        var m = CompanyNameMatcher.Match("UNKNOWN STORE LLC", null, [Takoma, SAndA]);
        Assert.Equal(CompanyMatchKind.None, m.Match);
        Assert.Null(m.CompanyId);
        Assert.Empty(m.Candidates);
    }

    [Fact]
    public void Should_ReturnAmbiguous_When_TwoCompaniesShareANormalisedName()
    {
        var m = CompanyNameMatcher.Match("SA CONVENIENCE MARKET", null,
            [Co(3, true, "SA Convenience Market"), SAndA, Co(1, true, "S & A Convenience Market LLC")]);
        Assert.Equal(CompanyMatchKind.Ambiguous, m.Match);
        Assert.Null(m.CompanyId);
        Assert.Equal([1, 2, 3], m.Candidates);
    }

    [Fact]
    public void Should_IgnoreCompany_When_NotMatchable()
    {
        var m = CompanyNameMatcher.Match("SA CONVENIENCE MARKET", null, [Co(2, false, "S & A Convenience Market LLC")]);
        Assert.Equal(CompanyMatchKind.None, m.Match);
        Assert.Empty(m.Candidates);
    }

    [Fact]
    public void Should_ReturnNone_When_PrintedNameNormalisesToNothing()
    {
        var m = CompanyNameMatcher.Match("LLC", "THE CO.", [Takoma, Co(9, true, null, "", "INC")]);
        Assert.Equal(CompanyMatchKind.None, m.Match);
        Assert.Null(m.CompanyId);
    }

    [Fact]
    public void Should_KeepPrintedNamesAsPrinted_When_Matching()
    {
        var m = CompanyNameMatcher.Match("  sa convenience market ", "DBA Zemen Market", [SAndA]);
        Assert.Equal("  sa convenience market ", m.PrintedName);
        Assert.Equal("DBA Zemen Market", m.DbaName);
    }
}
