namespace QbAutopost.Core.Companies;

/// <param name="Files">Full paths of the .qbw files directly inside the folder (at least one).</param>
public sealed record ScannedFolder(string FolderName, string? Code, string DisplayName, IReadOnlyList<string> Files);

/// <param name="NoCompanyFile">Names of subfolders that hold no .qbw file.</param>
public sealed record ScanResult(IReadOnlyList<ScannedFolder> Folders, IReadOnlyList<string> NoCompanyFile);

public sealed class CompanyRootUnavailableException(string rootFolder, Exception? inner)
    : Exception($"The company root folder '{rootFolder}' cannot be read.", inner)
{
    public string RootFolder { get; } = rootFolder;
}

public sealed record KnownCompanyFolder(int Id, string FolderName, string? FilePath, CompanyState State, bool IsDefault);

/// <param name="CompanyId">0 = a new company.</param>
public sealed record FolderChange(int CompanyId, ScannedFolder Folder, string? FilePath, CompanyState State);

public sealed record ScanPlan(
    IReadOnlyList<FolderChange> Add,
    IReadOnlyList<FolderChange> Update,
    IReadOnlyList<int> Missing,
    IReadOnlyList<string> NoCompanyFile)
{
    public int Ambiguous => Add.Concat(Update).Count(c => c.State == CompanyState.Ambiguous);
}
