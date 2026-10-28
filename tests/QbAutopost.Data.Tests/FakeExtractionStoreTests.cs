using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

public sealed class FakeExtractionStoreTests : ExtractionStoreContract
{
    protected override (ISavedRequestStore Requests, IUploadStore Uploads) CreateStores()
    {
        var store = new FakeSavedRequestStore();
        return (store, store);
    }
}
