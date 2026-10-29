using System.Globalization;
using System.Text;

namespace QbAutopost.Core.Extraction;

/// <summary>
/// Makes a client-supplied file name safe to store and show (spec §6.8). Never used as a path. T-1212b (Ruling R11):
/// unpaired surrogates and invisible characters (format Cf, line and paragraph separators) are dropped too.
/// </summary>
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
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsHighSurrogate(c))
            {
                // A pair is kept whole; a lone half is dropped.
                if (i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                {
                    clean.Append(c).Append(name[i + 1]);
                    i++;
                }

                continue;
            }

            if (c is > '\u001F' and < '\u007F' or > '\u009F' && !char.IsLowSurrogate(c) && !IsInvisible(c))
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

    private static bool IsInvisible(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
}
