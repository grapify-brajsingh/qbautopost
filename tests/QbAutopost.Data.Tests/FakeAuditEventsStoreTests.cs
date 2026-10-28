using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Data.Tests;

public sealed class FakeAuditEventsStoreTests : AuditEventsStoreContract
{
    protected override ISavedRequestStore CreateStore() => new FakeSavedRequestStore();

    protected override Task<UserAuditEntry> AddUserAuditAsync(ISavedRequestStore store, UserAuditEntry entry) =>
        Task.FromResult(((FakeSavedRequestStore)store).AddUserAudit(entry));
}
