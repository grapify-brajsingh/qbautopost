using System.Text;

namespace QbAutopost.Core.Extraction;

/// <summary>Makes a client-supplied file name safe to store and show (spec §6.8). Never used as a path.</summary>
public static class UploadFileName
{
    public const int MaxLength = 255;
    public const string Fallback = "upload.pdf";

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return Fallback;
        }

        var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..];
        var clean = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is > '\u001F' and < '\u007F' or > '\u009F')
            {
                clean.Append(c);
            }
        }

        var text = clean.ToString().Trim();
        if (text.Length > MaxLength)
        {
            var cut = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
            text = text[..cut].Trim();
        }

        return text.Length == 0 ? Fallback : text;
    }
}
