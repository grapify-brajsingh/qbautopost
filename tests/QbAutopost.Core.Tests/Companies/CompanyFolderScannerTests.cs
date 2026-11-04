using QbAutopost.Core.Companies;

namespace QbAutopost.Core.Tests.Companies;

/// <summary>M17 spec §4.2: one-level scan of Companies:RootFolder and reconcile with known companies (real temp folders).</summary>
public sealed class CompanyFolderScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("qbautopost-scan-").FullName;
    private readonly CompanyFolderScanner _scanner = new(new FileSystemCompanyFolderSource());

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Make(string folder, params string[] files)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "x");
    }

    private string At(string folder, string file) => Path.Combine(_root, folder, file);

    private static KnownCompanyFolder Known(string folder, string? path, CompanyState state = CompanyState.Active,
        bool isDefault = false, int id = 1) => new(id, folder, path, state, isDefault);

    private ScanResult ScanRoot() => _scanner.Scan(_root);

    private const string Takoma = "18_Takoma Park Grocery Store Inc";
    private const string SAndA = "440_S & A Convenience Market LLC";

    [Fact]
    public void Should_FindTwoCompaniesWithCodes_When_EachSubfolderHoldsOneQbw()
    {
        Make(Takoma, "Takoma Park Grocery Store Inc.qbw");
        Make(SAndA, "S & A Convenience Market LLC.qbw");

        var scan = ScanRoot();

        Assert.Equal(2, scan.Folders.Count);
        Assert.Contains(scan.Folders, f => f.Code == "18" && f.DisplayName == "Takoma Park Grocery Store Inc");
        Assert.Contains(scan.Folders, f => f.Code == "440" && f.DisplayName == "S & A Convenience Market LLC");
        var plan = CompanyFolderScanner.Reconcile(scan, []);
        Assert.Equal(2, plan.Add.Count);
        Assert.All(plan.Add, c => Assert.Equal(CompanyState.Active, c.State));
    }

    [Fact]
    public void Should_AddNothing_When_ScannedTwice()
    {
        Make(Takoma, "T.qbw");
        Make(SAndA, "S.qbw");
        var known = new[]
        {
            Known(Takoma, At(Takoma, "T.qbw"), id: 1),
            Known(SAndA, At(SAndA, "S.qbw"), id: 2),
        };

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), known);

        Assert.Empty(plan.Add);
        Assert.Empty(plan.Update);
        Assert.Empty(plan.Missing);
    }

    [Theory]
    [InlineData("18_Takoma Park Grocery Store Inc", "18", "Takoma Park Grocery Store Inc")]
    [InlineData("Main Street Deli", null, "Main Street Deli")]
    [InlineData("440_S & A Convenience Market LLC", "440", "S & A Convenience Market LLC")]
    [InlineData("x18_Shop", null, "x18_Shop")]
    public void Should_SplitCodeAndName_When_FolderStartsWithDigitsAndUnderscore(string folder, string? code, string name)
    {
        var (c, n) = CompanyFolderScanner.ParseFolderName(folder);

        Assert.Equal(code, c);
        Assert.Equal(name, n);
    }

    [Fact]
    public void Should_IgnoreSideFiles_When_FolderHoldsTlgNdQbbQbmDsn()
    {
        Make("A", "Company.qbw", "Company.qbw.tlg", "Company.qbw.nd", "Company.qbb", "Company.qbm", "Company.qbw.dsn");

        var folder = Assert.Single(ScanRoot().Folders);

        Assert.Equal([At("A", "Company.qbw")], folder.Files);
    }

    [Fact]
    public void Should_CountUpperCaseExtension_When_FileIsDotQBW()
    {
        Make("A", "Company.QBW");

        var folder = Assert.Single(ScanRoot().Folders);

        Assert.Equal([At("A", "Company.QBW")], folder.Files);
    }

    [Fact]
    public void Should_IgnoreNestedFolders_When_QbwIsTwoLevelsDown()
    {
        Make(Path.Combine("A", "old"), "x.qbw");

        var scan = ScanRoot();

        Assert.Empty(scan.Folders);
        Assert.Equal(["A"], scan.NoCompanyFile);
    }

    [Fact]
    public void Should_ReportNoCompanyFile_When_SubfolderHasNoQbw()
    {
        Make("Empty", "notes.txt");
        Make("B", "b.qbw");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), []);

        Assert.Equal(["Empty"], plan.NoCompanyFile);
        Assert.Single(plan.Add);
    }

    [Fact]
    public void Should_MarkAmbiguousWithBothCandidates_When_SubfolderHoldsTwoQbw()
    {
        Make("A", "one.qbw", "two.qbw");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), []);

        var add = Assert.Single(plan.Add);
        Assert.Equal(CompanyState.Ambiguous, add.State);
        Assert.Null(add.FilePath);
        Assert.Equal([At("A", "one.qbw"), At("A", "two.qbw")], add.Folder.Files);
        Assert.Equal(1, plan.Ambiguous);
    }

    [Fact]
    public void Should_KeepChosenFile_When_AmbiguousFolderIsRescannedAfterChoice()
    {
        Make("A", "one.qbw", "two.qbw");
        var chosen = At("A", "two.qbw");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), [Known("A", chosen, CompanyState.Ambiguous)]);

        var update = Assert.Single(plan.Update);
        Assert.Equal(CompanyState.Active, update.State);
        Assert.Equal(chosen, update.FilePath);
        Assert.Equal(2, update.Folder.Files.Count);
        Assert.Empty(plan.Add);
        Assert.Empty(plan.Missing);
    }

    [Fact]
    public void Should_UpdateFilePath_When_QbwWasRenamed()
    {
        Make("A", "new.qbw");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), [Known("A", At("A", "old.qbw"))]);

        var update = Assert.Single(plan.Update);
        Assert.Equal(At("A", "new.qbw"), update.FilePath);
        Assert.Equal(CompanyState.Active, update.State);
    }

    [Fact]
    public void Should_MarkAmbiguous_When_DefaultCompanyFolderHoldsADifferentQbw()
    {
        Make("A", "other.qbw");
        var configured = At("A", "default.qbw");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), [Known("A", configured, isDefault: true)]);

        var update = Assert.Single(plan.Update);
        Assert.Equal(CompanyState.Ambiguous, update.State);
        Assert.Equal(configured, update.FilePath);
    }

    [Fact]
    public void Should_MarkMissing_When_SubfolderDisappeared()
    {
        Make("A", "a.qbw");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(),
            [Known("A", At("A", "a.qbw"), id: 1), Known("Gone", At("Gone", "g.qbw"), id: 7)]);

        Assert.Equal([7], plan.Missing);
        Assert.Empty(plan.Update);
    }

    [Fact]
    public void Should_MarkMissing_When_SubfolderNoLongerHoldsAQbw()
    {
        Make("A", "readme.txt");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), [Known("A", At("A", "a.qbw"), id: 3)]);

        Assert.Equal([3], plan.Missing);
    }

    [Fact]
    public void Should_NotReportMissingAgain_When_CompanyIsAlreadyMissing()
    {
        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), [Known("Gone", null, CompanyState.Missing, id: 7)]);

        Assert.Empty(plan.Missing);
    }

    [Fact]
    public void Should_ReturnToActive_When_MissingSubfolderReappears()
    {
        Make("A", "a.qbw");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), [Known("A", At("A", "a.qbw"), CompanyState.Missing)]);

        var update = Assert.Single(plan.Update);
        Assert.Equal(CompanyState.Active, update.State);
        Assert.Equal(At("A", "a.qbw"), update.FilePath);
    }

    [Fact]
    public void Should_MatchKnownFolder_When_OnlyCaseDiffers()
    {
        Make("18_TAKOMA PARK GROCERY STORE INC", "T.QBW");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(),
            [Known(Takoma, At("18_TAKOMA PARK GROCERY STORE INC", "t.qbw"))]);

        Assert.Empty(plan.Add);
        Assert.Empty(plan.Missing);
        Assert.Empty(plan.Update);
    }

    [Fact]
    public void Should_NotChangeState_When_CompanyIsDisabled()
    {
        Make("A", "new.qbw");

        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), [Known("A", At("A", "old.qbw"), CompanyState.Disabled)]);

        var update = Assert.Single(plan.Update);
        Assert.Equal(CompanyState.Disabled, update.State);
        Assert.Equal(At("A", "new.qbw"), update.FilePath);
    }

    [Fact]
    public void Should_NotMarkDisabledMissing_When_SubfolderDisappeared()
    {
        var plan = CompanyFolderScanner.Reconcile(ScanRoot(), [Known("Gone", null, CompanyState.Disabled)]);

        Assert.Empty(plan.Missing);
    }

    [Fact]
    public void Should_ThrowRootUnavailable_When_RootDoesNotExist()
    {
        var missing = Path.Combine(_root, "nope");

        var ex = Assert.Throws<CompanyRootUnavailableException>(() => _scanner.Scan(missing));

        Assert.Equal(missing, ex.RootFolder);
    }
}
