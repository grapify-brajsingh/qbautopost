namespace QbAutopost.Core.Companies;

public interface ICompanyFolderSource
{
    /// <summary>Immediate subfolder names. Throws IOException/UnauthorizedAccessException/DirectoryNotFoundException when the root cannot be read.</summary>
    IReadOnlyList<string> Subfolders(string rootFolder);

    /// <summary>Full paths of the files directly in <paramref name="folderPath"/> whose extension is .qbw (OrdinalIgnoreCase); not recursive.</summary>
    IReadOnlyList<string> CompanyFiles(string folderPath);
}

public sealed class FileSystemCompanyFolderSource : ICompanyFolderSource
{
    public IReadOnlyList<string> Subfolders(string rootFolder) =>
        Directory.EnumerateDirectories(rootFolder, "*", SearchOption.TopDirectoryOnly)
            .Select(d => Path.GetFileName(d))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<string> CompanyFiles(string folderPath) =>
        Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetExtension(f).Equals(".qbw", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
