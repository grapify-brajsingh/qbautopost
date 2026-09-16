using System.Text;
using System.Text.Json;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Store;

/// <summary>Write-to-temp-then-rename so a crash never leaves a half-written shared file (spec §13).</summary>
public static class AtomicFile
{
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

        File.Move(temp, fullPath, overwrite: true);
    }

    public static void WriteJson<T>(string path, T value) =>
        WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions.Default));
}
