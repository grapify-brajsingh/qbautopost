using System.Globalization;
using ClosedXML.Excel;

namespace QbAutopost.Core.Extract;

/// <summary>
/// Reads the first worksheet of an <c>.xlsx</c> file into rows of strings (spec FR-3), the same shape
/// <see cref="CsvGrid"/> produces. Blank rows are dropped. Cell values are rendered without the workbook's
/// display formats, so the result does not depend on the machine's culture:
/// numbers → invariant digits, dates → <c>yyyy-MM-dd</c> (<see cref="IsoDateFormat"/>), formulas → their saved result.
/// </summary>
public static class XlsxGrid
{
    public const string IsoDateFormat = "yyyy-MM-dd";

    /// <summary>Largest magnitude a <see cref="decimal"/> can hold; larger numbers are passed through as text and fail money parsing.</summary>
    private const double DecimalLimit = 7.9e28;

    public static IReadOnlyList<string[]> Read(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);
        var used = sheet.RangeUsed(XLCellsUsedOptions.Contents);
        if (used is null)
        {
            return [];
        }

        var firstColumn = used.RangeAddress.FirstAddress.ColumnNumber;
        var lastColumn = used.RangeAddress.LastAddress.ColumnNumber;
        var rows = new List<string[]>();
        foreach (var row in used.Rows())
        {
            var rowNumber = row.RowNumber();
            var cells = new string[lastColumn - firstColumn + 1];
            for (var c = firstColumn; c <= lastColumn; c++)
            {
                cells[c - firstColumn] = Text(sheet.Cell(rowNumber, c));
            }

            if (cells.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            {
                rows.Add(cells);
            }
        }

        return rows;
    }

    private static string Text(IXLCell cell)
    {
        // A formula's saved result is used as-is; the workbook is never recalculated. No saved result → blank → row error.
        var value = cell.HasFormula ? cell.CachedValue : cell.Value;
        return value.Type switch
        {
            XLDataType.Blank => "",
            XLDataType.Text => value.GetText(),
            XLDataType.Number => Number(value.GetNumber()),
            XLDataType.DateTime => DateOnly.FromDateTime(value.GetDateTime()).ToString(IsoDateFormat, CultureInfo.InvariantCulture),
            XLDataType.Boolean => value.GetBoolean() ? "TRUE" : "FALSE",
            XLDataType.TimeSpan => value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),
            XLDataType.Error => $"#ERROR({value.GetError()})",
            _ => "",
        };
    }

    /// <summary>
    /// A double → decimal conversion keeps 15 significant digits, which removes binary noise such as
    /// <c>0.30000000000000004</c> without rounding cents; more than two decimals still fails money parsing.
    /// </summary>
    private static string Number(double number) =>
        Math.Abs(number) < DecimalLimit
            ? ((decimal)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);
}
