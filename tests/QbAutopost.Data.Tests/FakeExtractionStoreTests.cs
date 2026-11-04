using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

public sealed class FakeExtractionStoreTests : ExtractionStoreContract
{
    private FakeSavedRequestStore? _store;

    protected override (ISavedRequestStore Requests, IUploadStore Uploads) CreateStores()
    {
        var store = _store = new FakeSavedRequestStore();
        return (store, store);
    }

    /// <summary>The fake has no foreign key: any id will do.</summary>
    protected override Task<int> SeedCompanyAsync() => Task.FromResult(11);

    /// <summary>The fake has no column: the company it keeps on the record.</summary>
    protected override async Task<int?> StoredCompanyIdAsync(Guid extractionId) =>
        (await _store!.GetExtractionAsync(extractionId, default))?.Company?.CompanyId;
}