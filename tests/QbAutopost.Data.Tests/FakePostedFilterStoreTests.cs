using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

public sealed class FakePostedFilterStoreTests : PostedFilterStoreContract
{
    protected override ISavedRequestStore CreateStore() => new FakeSavedRequestStore();
}
