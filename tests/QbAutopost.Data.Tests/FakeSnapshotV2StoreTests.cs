using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

public sealed class FakeSnapshotV2StoreTests : SnapshotV2StoreContract
{
    protected override ISavedRequestStore CreateStore() => new FakeSavedRequestStore();
}
