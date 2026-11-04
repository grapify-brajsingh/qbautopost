using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;

namespace QbAutopost.Core.Tests.Companies;

public sealed class CompanyContextTests
{
    [Fact]
    public void Should_HaveNoTarget_When_CompanyIsTheDefault()
    {
        var context = new CompanyContext(1, "Tropicana", @"C:\qb\Tropicana.QBW", "rules.json", "ledger.json") { IsDefault = true };

        Assert.Null(context.Target);
    }

    [Fact]
    public void Should_TargetItsFile_When_CompanyIsNotTheDefault()
    {
        var context = new CompanyContext(2, "Takoma", @"C:\qb\Takoma.QBW", "rules.json", "ledger.json");

        Assert.Equal(new QbTarget(@"C:\qb\Takoma.QBW"), context.Target);
    }
}
