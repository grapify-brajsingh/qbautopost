using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Pipeline;

/// <summary>
/// T-914 (api-v1 FR-A-7): folder validation — F1, the statement parse and the G1 reconcile gate, with no job
/// queued, nothing written into the folder and nothing sent to QuickBooks.
/// <para>
/// The point of it is that a caller can learn whether a folder would run <i>before</i> committing to a job, and get
/// the same verdict the job would reach. A validate that passed and a job that then held a statement would be worse
/// than no validate at all.
/// </para>
/// </summary>
public sealed class FolderValidatorTests
{
    private static FolderValidator Validator() => new(TestStatementReader.Create());

    [Fact]
    public async Task Should_ReportEveryFolderError_When_TheFolderIsNotAJobFolder()
    {
        using var temp = new TempJobFolder();

        var result = await Validator().ValidateAsync(temp.Folder, Fixtures.SampleRules(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("requirement.txt is missing", result.Errors);
        Assert.Contains("statements folder is missing", result.Errors);
        Assert.Empty(result.Statements);
    }

    [Fact]
    public async Task Should_ReadEveryStatementAndPassG1_When_TheSampleFolderIsValid()
    {
        using var temp = TempJobFolder.FromSample();

        var result = await Validator().ValidateAsync(temp.Folder, Fixtures.SampleRules(), CancellationToken.None);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Equal(2, result.Statements.Count);
        Assert.All(result.Statements, s => Assert.True(s.Rows > 0, s.File));
        Assert.All(result.Statements, s => Assert.True(s.Reconcile?.Ok, s.File + ": " + s.Reconcile?.Message));
    }

    /// <summary>
    /// FR-A-7: "no <c>output/</c> written outside a temp copy". <see cref="Core.Jobs.FolderReader.Read"/> creates it,
    /// so a validate that used it unchanged would leave a folder behind on a caller's read-only share.
    /// </summary>
    [Fact]
    public async Task Should_LeaveTheFolderUntouched_When_AFolderIsValidated()
    {
        using var temp = TempJobFolder.FromSample();
        var before = Directory.GetFileSystemEntries(temp.Folder, "*", SearchOption.AllDirectories).Order().ToList();

        await Validator().ValidateAsync(temp.Folder, Fixtures.SampleRules(), CancellationToken.None);

        Assert.False(Directory.Exists(temp.PathOf("output")));
        Assert.Equal(before, Directory.GetFileSystemEntries(temp.Folder, "*", SearchOption.AllDirectories).Order().ToList());
    }

    [Fact]
    public async Task Should_HoldTheStatement_When_TheBalanceChainDoesNotReconcile()
    {
        using var temp = TempJobFolder.FromSample();
        var statement = temp.PathOf("statements", "chase-checking-4521.csv");
        File.WriteAllText(statement, File.ReadAllText(statement).Replace("9980.45", "9999.99", StringComparison.Ordinal));

        var result = await Validator().ValidateAsync(temp.Folder, Fixtures.SampleRules(), CancellationToken.None);

        Assert.False(result.Ok);
        var held = Assert.Single(result.Statements, s => s.IsHeld);
        Assert.Equal(HoldReasons.ReconcileFailed, held.HoldReason);
    }

    [Fact]
    public async Task Should_ListTheFileAsUnreadable_When_TheStatementsFolderHoldsAnUnsupportedFile()
    {
        using var temp = TempJobFolder.FromSample();
        temp.WithFile(Path.Combine("statements", "notes.txt"), "not a statement");

        var result = await Validator().ValidateAsync(temp.Folder, Fixtures.SampleRules(), CancellationToken.None);

        Assert.Contains(result.Unreadable, u => u.File == "statements/notes.txt");
    }

    [Fact]
    public async Task Should_ReportTheFolderIsRequired_When_NoFolderIsGiven()
    {
        var result = await Validator().ValidateAsync(null, Fixtures.SampleRules(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("folder is required", result.Errors);
    }
}
