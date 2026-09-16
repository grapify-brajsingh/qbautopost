using System.Text;
using System.Text.Json;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Store;

/// <summary>
/// Write-to-temp-then-rename so a crash never leaves a half-written shared file (spec §13).
/// On Windows a reader and the replacing rename can briefly block each other (sharing violation), e.g. while a client
/// polls <c>GET /jobs/{id}</c> during a run; both sides therefore share delete access and retry for a short time.
/// </summary>
public static class AtomicFile
{
    // Windows refuses to replace a file while any handle to it is open, so the rename waits for readers to finish:
    // growing pauses of 10…100 ms, about 1.5 s in total.
    private const int Attempts = 20;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static void WriteAllText(string path, string content, Encoding? encoding = null)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, encoding ?? Utf8NoBom))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        Retry(() => File.Move(temp, fullPath, overwrite: true));
    }

    public static void WriteJson<T>(string path, T value) =>
        WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions.Default));

    /// <summary>Reads a file that another thread may be replacing with <see cref="WriteAllText"/>.</summary>
    public static string ReadAllText(string path)
    {
        var text = "";
        Retry(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        });
        return text;
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < Attempts
                                       && ex is UnauthorizedAccessException or IOException
                                       && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(Math.Min(10 * attempt, 100));
            }
        }
    }
}
