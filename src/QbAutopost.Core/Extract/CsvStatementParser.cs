using QbAutopost.Core.Mapping;

namespace QbAutopost.Core.Extract;

/// <summary>Parses a <c>.csv</c> statement with the layouts from <c>rules.json</c> (spec FR-3). Never calls Hermes.</summary>
public sealed class CsvStatementParser(IReadOnlyDictionary<string, CsvLayout> layouts)
{
    private readonly StatementGridParser _grid = new(layouts);

    public StatementParseResult Parse(string path) =>
        _grid.Parse(Path.GetFileName(path), CsvGrid.Parse(File.ReadAllText(path)));
}
