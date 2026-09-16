namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>A throw-away directory under the temp folder; deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "qbautopost-api-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>A copy of the sample job (without any <c>output/</c>) under this directory; returns its folder.</summary>
    public string CopySampleJob(string jobId = "2026-08-tropicana")
    {
        var target = Combine(jobId);
        foreach (var source in Directory.EnumerateFiles(Fixtures.SampleJob, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(Fixtures.SampleJob, source);
            if (relative.StartsWith("output", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = System.IO.Path.Combine(target, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }

        return target;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
