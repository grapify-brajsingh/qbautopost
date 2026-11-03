using QbAutopost.Core.Companies;

namespace QbAutopost.Data.Tests;

/// <summary>T-1703 (M17 spec 4.3, 4.4): the company store, pinned on the fake and on PostgreSQL.</summary>
public abstract class CompanyStoreContract
{
    protected static readonly DateTime Now = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    protected abstract ICompanyStore CreateStore();

    /// <summary>A saved request (or the fake's stand-in) with no company, made before the store ensures the default.</summary>
    protected abstract Task<long> SeedUnassignedRequestAsync();

    /// <summary>The company id and source the request carries now.</summary>
    protected abstract Task<(int? CompanyId, CompanySource? Source)> ReadRequestCompanyAsync(long requestId);

    private static NewDefaultCompany DefaultCompany() =>
        new("Tropicana Market", @"C:\QB\Tropicana.qbw", null, null);

    private static ScannedFolder Folder(string name, params string[] files)
    {
        var (code, display) = CompanyFolderScanner.ParseFolderName(name);
        return new ScannedFolder(name, code, display, files.Length == 0 ? [$@"C:\Root\{name}\{name}.qbw"] : files);
    }

    private static ScanPlan PlanFor(IReadOnlyList<Company> known, params ScannedFolder[] folders) =>
        CompanyFolderScanner.Reconcile(
            new ScanResult(folders, []),
            known.Where(c => c.FolderName is not null)
                .Select(c => new KnownCompanyFolder(c.Id, c.FolderName!, c.FilePath, c.State, c.IsDefault)).ToList());

    private static async Task<IReadOnlyList<Company>> ScanAsync(ICompanyStore store, params ScannedFolder[] folders)
    {
        var plan = PlanFor(await store.ListAsync(default), folders);
        return (await store.ApplyScanAsync(plan, "user-1", Now, default)).Items;
    }

    [ContractFact]
    public async Task Should_CreateDefaultOnceAndBackfill_When_TableIsEmpty()
    {
        var store = CreateStore();
        var requestId = await SeedUnassignedRequestAsync();

        var first = await store.EnsureDefaultAsync(DefaultCompany(), Now, default);
        var second = await store.EnsureDefaultAsync(DefaultCompany(), Now.AddMinutes(1), default);

        Assert.True(first.Created);
        Assert.True(first.Company.IsDefault);
        Assert.Equal(CompanyState.Active, first.Company.State);
        Assert.Equal(CompanyAccess.Approved, first.Company.Access);
        Assert.True(first.Company.CanOpenSession);
        Assert.Equal(@"C:\QB\Tropicana.qbw", first.Company.FilePath);
        Assert.Equal(1, first.Backfilled);
        Assert.False(second.Created);
        Assert.Equal(0, second.Backfilled);
        Assert.Equal(first.Company.Id, second.Company.Id);
        Assert.Single(await store.ListAsync(default));

        var (companyId, source) = await ReadRequestCompanyAsync(requestId);
        Assert.Equal(first.Company.Id, companyId);
        Assert.Equal(CompanySource.Migrated, source);
    }

    [ContractFact]
    public async Task Should_AddActiveAndAmbiguousCompanies_When_ScanPlanApplied()
    {
        var store = CreateStore();

        var applied = await store.ApplyScanAsync(
            PlanFor([], Folder("100_Alpha Inc"), Folder("200_Beta LLC"), Folder("300_Gamma", @"C:\Root\300_Gamma\a.qbw", @"C:\Root\300_Gamma\b.qbw")),
            "user-1", Now, default);

        Assert.Equal(3, applied.Added);
        Assert.Equal(1, applied.Ambiguous);
        Assert.Equal(3, applied.AddedIds.Count);
        var gamma = Assert.Single(applied.Items, c => c.FolderName == "300_Gamma");
        Assert.Equal(CompanyState.Ambiguous, gamma.State);
        Assert.Null(gamma.FilePath);
        Assert.Equal([@"C:\Root\300_Gamma\a.qbw", @"C:\Root\300_Gamma\b.qbw"], gamma.CandidateFiles);
        var alpha = Assert.Single(applied.Items, c => c.FolderName == "100_Alpha Inc");
        Assert.Equal(CompanyState.Active, alpha.State);
        Assert.Equal("100", alpha.Code);
        Assert.Equal("Alpha Inc", alpha.DisplayName);
        Assert.Equal(CompanyAccess.Unchecked, alpha.Access);
        Assert.False(alpha.IsDefault);
        Assert.Equal(3, (await store.ListAsync(default)).Count);
    }

    [ContractFact]
    public async Task Should_UpsertByFolderName_When_SameFolderAppliedAgain()
    {
        var store = CreateStore();
        await ScanAsync(store, Folder("100_Alpha", @"C:\Root\100_Alpha\old.qbw"));

        var again = await store.ApplyScanAsync(
            PlanFor(await store.ListAsync(default), Folder("100_Alpha", @"C:\Root\100_Alpha\new.qbw")), "user-1", Now, default);

        Assert.Equal(0, again.Added);
        Assert.Equal(1, again.Updated);
        var all = await store.ListAsync(default);
        Assert.Equal(@"C:\Root\100_Alpha\new.qbw", Assert.Single(all).FilePath);
    }

    [ContractFact]
    public async Task Should_UpsertByFolderName_When_PlanAddsAFolderThatExistsInAnotherCase()
    {
        var store = CreateStore();
        await ScanAsync(store, Folder("Alpha"));

        // A racing scan planned "alpha" as new: the unique case-insensitive folder key turns it into an update.
        var plan = new ScanPlan([new FolderChange(0, Folder("alpha"), @"C:\Root\alpha\alpha.qbw", CompanyState.Active)], [], [], []);
        var applied = await store.ApplyScanAsync(plan, "user-1", Now, default);

        Assert.Equal(0, applied.Added);
        Assert.Single(await store.ListAsync(default));
    }

    [ContractFact]
    public async Task Should_RenameFolderWithoutNewRowOrMissing_When_OnlyTheCaseChanges()
    {
        var store = CreateStore();
        var first = (await ScanAsync(store, Folder("Alpha"))).Single();

        var plan = new ScanPlan([], [new FolderChange(first.Id, Folder("ALPHA", @"C:\Root\ALPHA\Alpha.qbw"), @"C:\Root\ALPHA\Alpha.qbw", CompanyState.Active)], [], []);
        var applied = await store.ApplyScanAsync(plan, "user-1", Now, default);

        var only = Assert.Single(await store.ListAsync(default));
        Assert.Equal(first.Id, only.Id);
        Assert.Equal("ALPHA", only.FolderName);
        Assert.Equal(CompanyState.Active, only.State);
        Assert.Equal(0, applied.Added);
        Assert.Equal(0, applied.Missing);
    }

    [ContractFact]
    public async Task Should_MarkMissing_When_PlanSaysMissing()
    {
        var store = CreateStore();
        var company = (await ScanAsync(store, Folder("100_Alpha"))).Single();

        var applied = await store.ApplyScanAsync(PlanFor(await store.ListAsync(default)), "user-1", Now, default);

        Assert.Equal(1, applied.Missing);
        var read = await store.GetAsync(company.Id, default);
        Assert.Equal(CompanyState.Missing, read!.State);
        Assert.False(read.CanOpenSession);
        Assert.True(read.RowVersion > company.RowVersion);
    }

    [ContractFact]
    public async Task Should_ReturnStale_When_RowVersionDoesNotMatch()
    {
        var store = CreateStore();
        var company = (await ScanAsync(store, Folder("100_Alpha"))).Single();

        var change = await store.UpdateAsync(company.Id, company.RowVersion - 1, new CompanyPatch(["Alpha Foods"], null, null), "user-1", Now, default);

        Assert.Equal(CompanyChangeKind.Stale, change.Kind);
        Assert.Empty((await store.GetAsync(company.Id, default))!.MatchNames);
    }

    [ContractFact]
    public async Task Should_ReturnNotFound_When_CompanyDoesNotExist()
    {
        var change = await CreateStore().UpdateAsync(999, 1, new CompanyPatch(null, null, true), "user-1", Now, default);

        Assert.Equal(CompanyChangeKind.NotFound, change.Kind);
    }

    [ContractFact]
    public async Task Should_RefuseFilePath_When_NotACandidate()
    {
        var store = CreateStore();
        var company = (await ScanAsync(store, Folder("300_Gamma", @"C:\Root\300_Gamma\a.qbw", @"C:\Root\300_Gamma\b.qbw"))).Single();

        var change = await store.UpdateAsync(company.Id, company.RowVersion, new CompanyPatch(null, @"C:\Elsewhere\x.qbw", null), "user-1", Now, default);

        Assert.Equal(CompanyChangeKind.Invalid, change.Kind);
        var read = await store.GetAsync(company.Id, default);
        Assert.Equal(CompanyState.Ambiguous, read!.State);
        Assert.Equal(company.RowVersion, read.RowVersion);
    }

    [ContractFact]
    public async Task Should_MakeActive_When_FilePathChosenFromCandidates()
    {
        var store = CreateStore();
        var company = (await ScanAsync(store, Folder("300_Gamma", @"C:\Root\300_Gamma\a.qbw", @"C:\Root\300_Gamma\b.qbw"))).Single();

        var change = await store.UpdateAsync(company.Id, company.RowVersion, new CompanyPatch(null, @"C:\Root\300_Gamma\b.qbw", null), "user-1", Now, default);

        Assert.Equal(CompanyChangeKind.Changed, change.Kind);
        Assert.Equal(CompanyState.Active, change.Company!.State);
        Assert.Equal(@"C:\Root\300_Gamma\b.qbw", change.Company.FilePath);
        Assert.Equal(CompanyAccess.Unchecked, change.Company.Access);
    }

    [ContractFact]
    public async Task Should_IncrementRowVersion_When_Changed()
    {
        var store = CreateStore();
        var company = (await ScanAsync(store, Folder("100_Alpha"))).Single();

        var change = await store.UpdateAsync(company.Id, company.RowVersion, new CompanyPatch([" Alpha Foods ", "alpha foods", ""], null, null), "user-1", Now, default);

        Assert.Equal(CompanyChangeKind.Changed, change.Kind);
        Assert.Equal(company.RowVersion + 1, change.Company!.RowVersion);
        Assert.Equal(["Alpha Foods"], change.Company.MatchNames);
        Assert.Equal(Now, change.Company.UpdatedUtc);
    }

    [ContractFact]
    public async Task Should_DisableAndReEnable_When_DisabledPatched()
    {
        var store = CreateStore();
        var company = (await ScanAsync(store, Folder("100_Alpha"))).Single();

        var off = await store.UpdateAsync(company.Id, company.RowVersion, new CompanyPatch(null, null, true), "user-1", Now, default);
        var on = await store.UpdateAsync(company.Id, off.Company!.RowVersion, new CompanyPatch(null, null, false), "user-1", Now, default);

        Assert.Equal(CompanyState.Disabled, off.Company.State);
        Assert.Equal(CompanyState.Active, on.Company!.State);
    }

    [ContractFact]
    public async Task Should_ReEnableAsMissing_When_ChosenFileIsNotOnDisk()
    {
        var store = CreateStore();
        var company = (await ScanAsync(store, Folder("100_Alpha"))).Single();
        var off = await store.UpdateAsync(company.Id, company.RowVersion, new CompanyPatch(null, null, true), "user-1", Now, default);

        var on = await store.UpdateAsync(company.Id, off.Company!.RowVersion, new CompanyPatch(null, null, false, FileExists: false), "user-1", Now, default);

        Assert.Equal(CompanyState.Missing, on.Company!.State);
    }

    [ContractFact]
    public async Task Should_ReEnableAsAmbiguous_When_NoFileWasChosenButCandidatesExist()
    {
        var store = CreateStore();
        var company = (await ScanAsync(store, Folder("100_Alpha", @"C:\Root\100_Alpha\a.qbw", @"C:\Root\100_Alpha\b.qbw"))).Single();
        var off = await store.UpdateAsync(company.Id, company.RowVersion, new CompanyPatch(null, null, true), "user-1", Now, default);

        var on = await store.UpdateAsync(company.Id, off.Company!.RowVersion, new CompanyPatch(null, null, false, FileExists: false), "user-1", Now, default);

        Assert.Equal(CompanyState.Ambiguous, on.Company!.State);
    }

    [ContractFact]
    public async Task Should_KeepRowVersionAndCountNoUpdate_When_RescanChangesNothingInAMultiFileCompany()
    {
        var store = CreateStore();
        var folder = Folder("100_Alpha", @"C:\Root\100_Alpha\a.qbw", @"C:\Root\100_Alpha\b.qbw");
        var company = (await ScanAsync(store, folder)).Single();
        var chosen = await store.UpdateAsync(company.Id, company.RowVersion, new CompanyPatch(null, folder.Files[1], null), "user-1", Now, default);

        var plan = PlanFor(await store.ListAsync(default), folder);
        var applied = await store.ApplyScanAsync(plan, "user-1", Now.AddMinutes(1), default);

        Assert.Equal((0, 0), (applied.Updated, applied.Items.Count));
        Assert.Equal(chosen.Company!.RowVersion, (await store.GetAsync(company.Id, default))!.RowVersion);
    }

    [ContractFact]
    public async Task Should_KeepDisabled_When_RacingAddResolvesToADisabledCompany()
    {
        var store = CreateStore();
        var folder = Folder("100_Alpha");
        var company = (await ScanAsync(store, folder)).Single();
        await store.UpdateAsync(company.Id, company.RowVersion, new CompanyPatch(null, null, true), "user-1", Now, default);

        var racing = new ScanPlan([new FolderChange(0, folder, folder.Files[0], CompanyState.Active)], [], [], []);
        await store.ApplyScanAsync(racing, "user-1", Now.AddMinutes(1), default);

        Assert.Equal(CompanyState.Disabled, (await store.GetAsync(company.Id, default))!.State);
    }

    [ContractFact]
    public async Task Should_RefuseDisable_When_CompanyIsTheDefault()
    {
        var store = CreateStore();
        var company = (await store.EnsureDefaultAsync(DefaultCompany(), Now, default)).Company;

        var change = await store.UpdateAsync(company.Id, company.RowVersion, new CompanyPatch(null, null, true), "user-1", Now, default);

        Assert.Equal(CompanyChangeKind.Invalid, change.Kind);
    }

    [ContractFact]
    public async Task Should_RecordAccessAndNames_When_AccessChecked()
    {
        var store = CreateStore();
        var company = (await ScanAsync(store, Folder("100_Alpha"))).Single();
        var checkedUtc = Now.AddMinutes(5);

        var change = await store.RecordAccessAsync(
            company.Id, new CompanyAccessCheck(CompanyAccess.Approved, "ok", "Alpha Inc", "Alpha Incorporated", checkedUtc), "user-1", default);

        Assert.Equal(CompanyChangeKind.Changed, change.Kind);
        var read = await store.GetAsync(company.Id, default);
        Assert.Equal(CompanyAccess.Approved, read!.Access);
        Assert.Equal("ok", read.AccessMessage);
        Assert.Equal("Alpha Inc", read.QbCompanyName);
        Assert.Equal("Alpha Incorporated", read.QbLegalName);
        Assert.Equal(checkedUtc, read.AccessCheckedUtc);
        Assert.True(read.CanOpenSession);
        Assert.Contains("Alpha Incorporated", read.ForMatching().Names);
    }

    [ContractFact]
    public async Task Should_ReturnNotFound_When_AccessCheckedForUnknownCompany()
    {
        var change = await CreateStore().RecordAccessAsync(
            999, new CompanyAccessCheck(CompanyAccess.Error, "x", null, null, Now), "user-1", default);

        Assert.Equal(CompanyChangeKind.NotFound, change.Kind);
    }

    [ContractFact]
    public async Task Should_ReturnLastScanTime_When_Scanned()
    {
        var store = CreateStore();
        Assert.Null(await store.LastScanUtcAsync(default));

        await store.ApplyScanAsync(PlanFor([], Folder("100_Alpha")), "user-1", Now, default);
        await store.ApplyScanAsync(PlanFor(await store.ListAsync(default)), "user-1", Now.AddHours(1), default);

        Assert.Equal(Now.AddHours(1), await store.LastScanUtcAsync(default));
    }
}
