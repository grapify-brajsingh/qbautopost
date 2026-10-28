using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

public sealed class FakePushIdStoreTests : PushIdStoreContract
{
    protected override ISavedRequestStore CreateStore() => new FakeSavedRequestStore();
}
