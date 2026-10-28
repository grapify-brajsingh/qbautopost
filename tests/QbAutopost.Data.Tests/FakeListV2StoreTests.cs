using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

public sealed class FakeListV2StoreTests : ListV2StoreContract
{
    protected override ISavedRequestStore CreateStore() => new FakeSavedRequestStore();
}
