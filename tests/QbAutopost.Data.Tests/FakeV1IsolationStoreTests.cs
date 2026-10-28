using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

public sealed class FakeV1IsolationStoreTests : V1IsolationStoreContract
{
    protected override ISavedRequestStore CreateStore() => new FakeSavedRequestStore();
}
