using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

public sealed class FakeListV2StoreTests : ListV2StoreContract
{
    private readonly FakeSavedRequestStore _store = new();

    protected override ISavedRequestStore CreateStore() => _store;

    protected override Task<int> NewCompanyIdAsync() => Task.FromResult(7);

    protected override Task SetCompanyAsync(ISavedRequestStore store, long requestId, int companyId)
    {
        _store.SetCompany(requestId, companyId, QbAutopost.Core.Companies.CompanySource.Chosen);
        return Task.CompletedTask;
    }
}
