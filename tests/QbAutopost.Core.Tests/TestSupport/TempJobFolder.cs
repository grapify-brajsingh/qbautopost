namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>A throw-away job folder under the temp directory; deleted on dispose.</summary>
internal sealed class TempJobFolder : IDisposable
{
    public TempJobFolder(string jobId = "2026-08-test")
    {
        Root = Path.Combine(Path.GetTempPath(), "qbautopost-tests", Guid.NewGuid().ToString("N"));
        Folder = Path.Combine(Root, jobId);
        Directory.CreateDirectory(Folder);
    }

    public string Root { get; }

    public string Folder { get; }

    /// <summary>A copy of the sample job (requirement + statements + invoices, never <c>output/</c>).</summary>
    public static TempJobFolder FromSample()
    {
        var temp = new TempJobFolder("2026-08-tropicana");
        foreach (var source in Directory.EnumerateFiles(Fixtures.SampleJob, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Fixtures.SampleJob, source);
            if (relative.StartsWith("output", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            temp.Copy(source, relative);
        }

        return temp;
    }

    public TempJobFolder WithFile(string relativePath, string content = "x")
    {
        var path = Path.Combine(Folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    public TempJobFolder WithDirectory(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(Folder, relativePath));
        return this;
    }

    public TempJobFolder Copy(string source, string relativePath)
    {
        var path = Path.Combine(Folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(source, path, overwrite: true);
        return this;
    }

    public string PathOf(params string[] parts) => Path.Combine([Folder, .. parts]);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
