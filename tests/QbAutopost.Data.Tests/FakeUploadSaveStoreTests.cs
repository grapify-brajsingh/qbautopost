using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Extraction;

namespace QbAutopost.Data.Tests;

public sealed class FakeUploadSaveStoreTests : UploadSaveStoreContract
{
    protected override IUploadStore CreateStore() => new FakeSavedRequestStore();
}
