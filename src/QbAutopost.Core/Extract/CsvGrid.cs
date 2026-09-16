using System.Text;

namespace QbAutopost.Core.Extract;

/// <summary>Minimal RFC 4180 reader: quoted fields, doubled quotes, CRLF/LF, leading BOM. Blank rows are dropped.</summary>
public static class CsvGrid
{
    public static IReadOnlyList<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var start = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r' or '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    EndRow(rows, row, field);
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            EndRow(rows, row, field);
        }

        return rows;
    }

    private static void EndRow(List<string[]> rows, List<string> row, StringBuilder field)
    {
        row.Add(field.ToString());
        field.Clear();
        if (row.Any(cell => !string.IsNullOrWhiteSpace(cell)))
        {
            rows.Add(row.ToArray());
        }

        row.Clear();
    }
}
