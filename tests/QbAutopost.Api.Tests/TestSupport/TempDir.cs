namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>A throw-away directory under the temp folder; deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "qbautopost-api-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>A copy of the sample job (without any <c>output/</c>) under this directory; returns its folder.</summary>
    public string CopySampleJob(string jobId = "2026-08-tropicana") => CopyJob(Fixtures.SampleJob, jobId);

    /// <summary>A copy of the job folder <paramref name="from"/> (without any <c>output/</c>) as <paramref name="jobId"/>.</summary>
    public string CopyJob(string from, string jobId)
    {
        var target = Combine(jobId);
        foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(from, source);
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

    /// <summary>
    /// Deletes the folder, retrying briefly. <b>Q-62</b>: this threw <c>IOException</c> on
    /// <c>qbautopost-yyyyMMdd.log</c> in roughly one full run out of three once T-912 added 42 more tests — always in
    /// a different, otherwise-passing test class. The cause is teardown, not behaviour: <c>WebApplicationFactory</c>
    /// returns from <c>Dispose</c> before Serilog's shared file sink has released the handle, and the folder goes a
    /// moment later. Retrying is the honest fix; the alternative is a suite that fails at random for a reason that
    /// has nothing to do with what any test asserts.
    /// <para>
    /// If the handle is still held after the last attempt the folder is left behind: it is under the machine's temp
    /// directory, so the cost is a few stale kilobytes, and failing a green test to report them would be worse.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
