using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.QbXml;

/// <summary>T-1710 (M17 spec 3): the <c>CompanyQueryRq</c> request and its reader.</summary>
public sealed class QbCompanyQueryTests
{
    [Fact]
    public void Should_MatchGoldenFile_When_Built() =>
        Assert.Equal(
            Fixtures.Read("qbxml", "company-query.golden.xml").ReplaceLineEndings("\n").TrimEnd(),
            QbCompanyQuery.Build().ReplaceLineEndings("\n").TrimEnd());

    [Fact]
    public void Should_ReadNames_When_CompanyRetPresent()
    {
        var info = QbCompanyQuery.Parse(Fixtures.Read("qbxml", "company-response.xml"));

        Assert.Equal(0, info.Status.Code);
        Assert.Equal("Takoma Park Grocery Store Inc", info.CompanyName);
        Assert.Equal("Takoma Park Grocery Store Incorporated", info.LegalCompanyName);
    }

    [Fact]
    public void Should_BeReadOnly_When_Built() => Assert.True(QbXmlRequests.IsReadOnly(QbCompanyQuery.Build()));
}
