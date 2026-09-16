using QbAutopost.Core.Store;

namespace QbAutopost.Core.Tests.Store;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qbautopost-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Should_ReplaceContent_When_WrittenTwice()
    {
        var path = Path.Combine(_dir, "result.json");

        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.Equal("second", AtomicFile.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Should_NeverFailOrReadPartialContent_When_ReadWhileBeingReplaced()
    {
        var path = Path.Combine(_dir, "result.json");
        var contents = new[] { new string('a', 5_000), new string('b', 6_000) };
        AtomicFile.WriteAllText(path, contents[0]);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                AtomicFile.WriteAllText(path, contents[i % 2]);
            }
        });
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                Assert.Contains(AtomicFile.ReadAllText(path), contents);
                reads++;
                Thread.Sleep(1); // a polling client, not a reader that never lets go of the file

            }

            return reads;
        });

        await writer;
        Assert.True(await reader > 0);
    }

    [Fact]
    public void Should_ThrowImmediately_When_FileDoesNotExist()
    {
        Directory.CreateDirectory(_dir);

        Assert.Throws<FileNotFoundException>(() => AtomicFile.ReadAllText(Path.Combine(_dir, "missing.json")));
    }
}
