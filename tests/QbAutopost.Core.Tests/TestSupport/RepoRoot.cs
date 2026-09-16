namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>Finds the repository root (the folder holding <c>QbAutopost.sln</c>) above the test output directory.</summary>
internal static class RepoRoot
{
    public static string Find()
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
