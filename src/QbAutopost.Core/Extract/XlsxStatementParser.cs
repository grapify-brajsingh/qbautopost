using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Extract;

/// <summary>
/// Parses an <c>.xlsx</c> statement (spec FR-3): first worksheet → <see cref="XlsxGrid"/> → the same layout logic as CSV.
/// Never calls Hermes. A workbook that cannot be opened is held whole.
/// </summary>
public sealed class XlsxStatementParser(IReadOnlyDictionary<string, CsvLayout> layouts)
{
    // Real date cells arrive as yyyy-MM-dd, so that form is accepted next to the layout's own format.
    private readonly StatementGridParser _grid = new(layouts, acceptIsoDates: true);

    public StatementParseResult Parse(string path)
    {
        var fileName = Path.GetFileName(path);
        IReadOnlyList<string[]> grid;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            grid = XlsxGrid.Read(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // SPEC-GAP T-301: ClosedXML throws several exception types for corrupt, encrypted or non-xlsx files.
            return new StatementParseResult
            {
                File = fileName,
                HoldReason = HoldReasons.UnreadableStatement,
                Errors = [$"workbook could not be read: {ex.GetType().Name}: {ex.Message}"],
            };
        }

        return _grid.Parse(fileName, grid);
    }
}
