namespace QbAutopost.Core.Tests.Architecture;

/// <summary>CLAUDE.md hard rule 2 / ADR-0001: COM stays in QbAutopost.QuickBooks.</summary>
public sealed class CoreIsolationTests
{
    private static readonly string[] Forbidden =
    [
        "System.Runtime.InteropServices",
        "GetTypeFromProgID",
        "ComImport",
        "ComVisible",
        "QBXMLRP2",
    ];

    [Fact]
    public void Should_NotReferenceCom_When_ScanningCoreSources()
    {
        var coreDir = Path.Combine(FindRepoRoot(), "src", "QbAutopost.Core");
        var sources = Directory
            .EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(coreDir, f));

        var offenders = sources
            .SelectMany(file => Forbidden
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(coreDir, file)}: {token}"))
            .ToList();

        Assert.Empty(offenders);
    }

    private static bool IsBuildOutput(string root, string file)
    {
        var first = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QbAutopost.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("QbAutopost.sln not found above the test output directory.");
    }
}
