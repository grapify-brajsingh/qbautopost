using QbAutopost.Core.Api;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Store;

/// <summary>
/// T-908 / FR-A-10: the evidence folder for one direct batch. CLAUDE.md rule 7 — every state transition is on disk
/// before the next step starts, and <c>request.json</c> is written <b>before the first qbXML leaves the process</b>,
/// so "who asked us to post this" survives a crash mid-post.
/// </summary>
public sealed class ApiBatchStoreTests : IDisposable
{
    private readonly TempJobFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private ApiBatchStore Store => new(Path.Combine(_dir.Root, "api-batches"));

    private static DirectPostResult Result(string batchId, JobStatus status) => new()
    {
        BatchId = batchId,
        Reference = "payroll-2026-09",
        Status = status,
        StartedUtc = new DateTime(2026, 9, 22, 9, 14, 2, DateTimeKind.Utc),
    };

    [Fact]
    public void Should_WriteTheRequestVerbatim_When_ABatchStarts()
    {
        var request = new DirectRequest { Reference = "payroll-2026-09", ControlTotal = 184.32m };

        Store.SaveRequest("api-payroll-2026-09#1", request);

        var file = Path.Combine(_dir.Root, "api-batches", "api-payroll-2026-09#1", ApiBatchStore.RequestFile);
        Assert.True(File.Exists(file));
        Assert.Contains("payroll-2026-09", File.ReadAllText(file));
    }

    [Fact]
    public void Should_ReadBackWhatWasWritten_When_AResultIsSaved()
    {
        var store = Store;
        store.SaveResult(Result("api-payroll-2026-09#1", JobStatus.Posted));

        var loaded = store.Load("api-payroll-2026-09#1");

        Assert.NotNull(loaded);
        Assert.Equal(JobStatus.Posted, loaded.Status);
        Assert.Equal("payroll-2026-09", loaded.Reference);
    }

    [Fact]
    public void Should_PreferTheResult_When_BothAStatusAndAResultExist()
    {
        // status.json is the live transition; result.json is the final word. A finished batch must not report posting.
        var store = Store;
        store.SaveStatus(Result("api-x#1", JobStatus.Posting));
        store.SaveResult(Result("api-x#1", JobStatus.Posted));

        Assert.Equal(JobStatus.Posted, store.Load("api-x#1")!.Status);
    }

    [Fact]
    public void Should_ReadTheStatus_When_TheBatchIsStillRunning()
    {
        var store = Store;
        store.SaveStatus(Result("api-x#1", JobStatus.Posting));

        Assert.Equal(JobStatus.Posting, store.Load("api-x#1")!.Status);
    }

    [Fact]
    public void Should_ReturnNull_When_TheBatchIsUnknown()
    {
        Assert.Null(Store.Load("api-never-happened#1"));
    }

    [Fact]
    public void Should_KeepTheQbXmlBesideTheResult_When_ABatchIsPosted()
    {
        var store = Store;
        store.SaveRequestQbXml("api-x#1", "<QBXML><request/></QBXML>");
        store.SaveResponseQbXml("api-x#1", "<QBXML><response/></QBXML>");

        var folder = Path.Combine(_dir.Root, "api-batches", "api-x#1");
        Assert.Contains("<request/>", File.ReadAllText(Path.Combine(folder, ApiBatchStore.RequestQbXmlFile)));
        Assert.Contains("<response/>", File.ReadAllText(Path.Combine(folder, ApiBatchStore.ResponseQbXmlFile)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("api-x/../../etc")]
    [InlineData("C:\\windows\\system32")]
    [InlineData("")]
    public void Should_Refuse_When_TheBatchIdWouldLeaveTheFolder(string batchId)
    {
        // The id arrives in a URL (FR-A-11). A store that takes it as a path is a traversal waiting to happen.
        Assert.Throws<ArgumentException>(() => Store.SaveResult(Result(batchId, JobStatus.Posted)));
        Assert.Throws<ArgumentException>(() => Store.Load(batchId));
    }

    [Fact]
    public void Should_ListNothing_When_NoBatchHasRun()
    {
        Assert.Empty(Store.All());
    }

    [Fact]
    public void Should_CountAttempts_When_TheSameReferenceIsPostedTwice()
    {
        var store = Store;
        store.SaveResult(Result("api-payroll-2026-09#1", JobStatus.Partial));

        // The next attempt must not reuse the id: the ledger and undo key off it (spec §3).
        Assert.Equal("api-payroll-2026-09#2", store.NextBatchId("api-payroll-2026-09"));
    }

    [Fact]
    public void Should_StartAtAttemptOne_When_TheReferenceIsNew()
    {
        Assert.Equal("api-payroll-2026-09#1", Store.NextBatchId("api-payroll-2026-09"));
    }
}
