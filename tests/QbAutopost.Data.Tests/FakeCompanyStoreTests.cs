using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Companies;

namespace QbAutopost.Data.Tests;

public sealed class FakeCompanyStoreTests : CompanyStoreContract
{
    private readonly FakeCompanyStore _store = new();

    protected override ICompanyStore CreateStore() => _store;

    protected override Task<long> SeedUnassignedRequestAsync() => Task.FromResult(_store.SeedUnassignedRequest());

    protected override Task<(int? CompanyId, CompanySource? Source)> ReadRequestCompanyAsync(long requestId) =>
        Task.FromResult(_store.RequestCompany(requestId));
}
