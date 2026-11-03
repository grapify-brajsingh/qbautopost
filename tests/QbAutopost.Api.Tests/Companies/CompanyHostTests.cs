using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Companies;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Companies;

namespace QbAutopost.Api.Tests.Companies;

public sealed class CompanyHostTests
{
    [Fact]
    public async Task Should_HaveARealDefaultCompany_When_HostWithoutDatabaseStarts()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthorizedClient();
        await client.GetAsync("/api/v1/health");

        var resolver = factory.Services.GetRequiredService<CompanyResolver>();
        var stored = (await factory.Companies.ListAsync(default)).Single();

        Assert.Equal((true, true, stored.Id), (resolver.HasDefault, stored.IsDefault, resolver.Default.Id));
        Assert.Same(resolver, factory.Services.GetRequiredService<ICompanyResolver>());
    }
}
