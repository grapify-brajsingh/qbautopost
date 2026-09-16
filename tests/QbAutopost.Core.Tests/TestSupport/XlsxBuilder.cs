using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>
/// Writes small <c>.xlsx</c> workbooks for parser tests. A row is an array of cell values:
/// <c>string</c>, <c>decimal</c>, <c>int</c>, <c>double</c>, <c>DateTime</c> (stored as a real date cell), <c>bool</c>,
/// <see cref="Formula"/> or <c>null</c> (blank).
/// </summary>
internal static class XlsxBuilder
{
    public static void Write(string path, params object?[][] rows) =>
        WriteSheets(path, ("Sheet1", rows));

    public static void WriteSheets(string path, params (string Name, object?[][] Rows)[] sheets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var results = new List<(int Sheet, string Cell, double Value)>();
        using (var workbook = new XLWorkbook())
        {
            for (var s = 0; s < sheets.Length; s++)
            {
                var (name, rows) = sheets[s];
                var sheet = workbook.AddWorksheet(name);
                for (var r = 0; r < rows.Length; r++)
                {
                    for (var c = 0; c < rows[r].Length; c++)
                    {
                        var cell = sheet.Cell(r + 1, c + 1);
                        Set(cell, rows[r][c]);
                        if (rows[r][c] is Formula { Calculated: true })
                        {
                            results.Add((s + 1, cell.Address.ToString()!, cell.Value.GetNumber()));
                        }
                    }
                }
            }

            workbook.SaveAs(path);
        }

        SaveFormulaResults(path, results);
    }

    /// <summary>
    /// Excel stores each formula with its last result (<c>&lt;v&gt;</c>); ClosedXML never writes it,
    /// so it is added to the sheet XML afterwards (numeric results only).
    /// </summary>
    private static void SaveFormulaResults(string path, IReadOnlyList<(int Sheet, string Cell, double Value)> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var group in results.GroupBy(r => r.Sheet))
        {
            var entryName = $"xl/worksheets/sheet{group.Key}.xml";
            var entry = zip.GetEntry(entryName)!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
            {
                xml = reader.ReadToEnd();
            }

            foreach (var (_, cell, value) in group)
            {
                var pattern = $@"(<x:c r=""{cell}""[^>]*><x:f>.*?</x:f>)";
                var saved = $"<x:v>{value.ToString("R", CultureInfo.InvariantCulture)}</x:v>";
                xml = Regex.Replace(xml, pattern, m => m.Value + saved);
            }

            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write(xml);
        }
    }

    private static void Set(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case string s:
                cell.Value = s;
                break;
            case decimal d:
                cell.Value = d;
                break;
            case int i:
                cell.Value = i;
                break;
            case double x:
                cell.Value = x;
                break;
            case DateTime dt:
                cell.Value = dt;
                cell.Style.DateFormat.Format = "mm/dd/yyyy";
                break;
            case bool b:
                cell.Value = b;
                break;
            case Formula f:
                cell.FormulaA1 = f.Text;
                break;
            default:
                throw new ArgumentException($"unsupported cell value type {value.GetType().Name}");
        }
    }
}

/// <summary>
/// A formula cell for <see cref="XlsxBuilder"/> (text without the leading <c>=</c>).
/// <paramref name="Calculated"/> = false saves it without a result, as a generator that never calculates would.
/// </summary>
internal sealed record Formula(string Text, bool Calculated = true);
