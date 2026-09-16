using System.Globalization;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Extract;

/// <summary>
/// Turns a grid of strings (first non-blank row = header) into statement lines using a
/// <see cref="CsvLayout"/> (spec FR-3). Shared by the CSV and XLSX parsers.
/// </summary>
/// <param name="layouts">Layouts from <c>rules.json.CsvLayouts</c>.</param>
/// <param name="acceptIsoDates">
/// Also accept an exact <c>yyyy-MM-dd</c> date next to the layout's format. Set by the XLSX parser, whose real date
/// cells arrive in that form; CSV text must match the layout's format (tracker Q-1, Q-23).
/// </param>
public sealed class StatementGridParser(IReadOnlyDictionary<string, CsvLayout> layouts, bool acceptIsoDates = false)
{
    public StatementParseResult Parse(string fileName, IReadOnlyList<string[]> grid)
    {
        if (grid.Count == 0)
        {
            return Held(fileName, HoldReasons.UnknownCsvLayout, "file has no rows");
        }

        var header = grid[0].Select(h => h.Trim()).ToArray();
        var matches = layouts.Where(l => l.Value.Matches(header)).ToList();
        if (matches.Count == 0)
        {
            return Held(fileName, HoldReasons.UnknownCsvLayout, $"no layout matches header: {string.Join(" | ", header)}");
        }

        if (matches.Count > 1)
        {
            // SPEC-GAP T-002: more than one layout fits → hold rather than pick one.
            return Held(fileName, HoldReasons.AmbiguousCsvLayout, $"layouts match: {string.Join(", ", matches.Select(m => m.Key))}");
        }

        var (layoutName, layout) = (matches[0].Key, matches[0].Value);
        var columns = new Columns(header);
        var missing = columns.MissingRequired(layout);
        if (missing.Count > 0)
        {
            return Held(fileName, HoldReasons.UnknownCsvLayout, $"layout '{layoutName}' columns missing: {string.Join(", ", missing)}") with
            {
                Kind = layout.Kind,
                Layout = layoutName,
            };
        }

        var dataRows = grid.Skip(1).ToList();
        var (last4, last4Error, last4Detail) = DetectLast4(fileName, layout, columns, dataRows);

        var rows = new List<StatementLine>();
        var errors = new List<string>();
        if (last4Detail is not null)
        {
            errors.Add(last4Detail);
        }

        for (var i = 0; i < dataRows.Count; i++)
        {
            var lineNo = i + 1;
            if (TryParseRow(dataRows[i], lineNo, fileName, last4 ?? "", layout, columns, acceptIsoDates, out var line, out var error))
            {
                rows.Add(line);
            }
            else
            {
                errors.Add($"row {lineNo}: {error}");
            }
        }

        var holdReason = last4Error
            ?? (last4 is null ? HoldReasons.UnknownAccount : null)
            ?? (errors.Count > 0 ? HoldReasons.UnparsableRows : null); // SPEC-GAP T-002: one bad row holds the file

        return new StatementParseResult
        {
            File = fileName,
            Kind = layout.Kind,
            Last4 = last4,
            Layout = layoutName,
            Rows = rows,
            HoldReason = holdReason,
            Errors = errors,
        };
    }

    /// <summary>
    /// Spec F5: file name first, else the layout's last-four column (all rows must agree).
    /// SPEC-GAP T-305: a column value that contradicts the file name holds the statement, as T2 does (Q-25).
    /// </summary>
    private static (string? Last4, string? Reason, string? Detail) DetectLast4(
        string fileName, CsvLayout layout, Columns columns, IReadOnlyList<string[]> dataRows)
    {
        var fromName = Last4Detector.FromFileName(fileName);
        if (layout.Last4Column is null)
        {
            return (fromName, null, null);
        }

        var values = dataRows
            .Select(r => Last4Detector.FromCell(columns.Cell(r, layout.Last4Column)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (fromName is not null)
        {
            var others = values.Where(v => v != fromName).ToList();
            return others.Count == 0
                ? (fromName, null, null)
                : (null, HoldReasons.ConflictingLast4, $"file name says {fromName}, account column says {string.Join(", ", others)}");
        }

        return values.Count switch
        {
            0 => (null, null, null),
            1 => (values[0], null, null),
            _ => (null, HoldReasons.ConflictingLast4, $"account column has several last-fours: {string.Join(", ", values)}"),
        };
    }

    private static bool TryParseRow(
        string[] cells, int lineNo, string fileName, string last4, CsvLayout layout, Columns columns, bool acceptIsoDates,
        out StatementLine line, out string error)
    {
        line = null!;
        error = "";

        var dateText = columns.Cell(cells, layout.DateColumn);
        if (!TryParseDate(dateText, layout.DateFormat, acceptIsoDates, out var date))
        {
            error = $"bad date '{dateText}'";
            return false;
        }

        var description = columns.Cell(cells, layout.DescriptionColumn);
        if (description.Length == 0)
        {
            error = "empty description";
            return false;
        }

        if (!TryParseAmount(cells, layout, columns, out var amount, out var direction, out error))
        {
            return false;
        }

        decimal? balance = null;
        if (layout.BalanceColumn is not null)
        {
            var balanceText = columns.Cell(cells, layout.BalanceColumn);
            if (balanceText.Length > 0)
            {
                if (!Money.TryParse(balanceText, out var parsed))
                {
                    error = $"bad balance '{balanceText}'";
                    return false;
                }

                balance = parsed;
            }
        }

        var checkNo = layout.CheckNoColumn is null ? "" : columns.Cell(cells, layout.CheckNoColumn);
        line = new StatementLine
        {
            SourceFile = fileName,
            Kind = layout.Kind,
            Last4 = last4,
            LineNo = lineNo,
            Date = date,
            Description = description,
            Direction = direction,
            Amount = amount,
            CheckNo = checkNo.Length == 0 ? null : checkNo,
            Balance = balance,
        };
        return true;
    }

    /// <summary>
    /// With a layout format the cell must match it exactly (SPEC-GAP T-301: no invariant fallback, tracker Q-1/Q-23);
    /// without one, invariant parsing (spec FR-3).
    /// </summary>
    private static bool TryParseDate(string text, string? format, bool acceptIsoDates, out DateOnly date)
    {
        if (format is null)
        {
            return DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        return DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
               || (acceptIsoDates
                   && DateOnly.TryParseExact(text, XlsxGrid.IsoDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date));
    }

    private static bool TryParseAmount(
        string[] cells, CsvLayout layout, Columns columns, out decimal amount, out Direction direction, out string error)
    {
        amount = 0m;
        direction = Direction.Debit;
        error = "";

        if (layout.AmountColumn is not null)
        {
            var text = columns.Cell(cells, layout.AmountColumn);
            if (!Money.TryParse(text, out var signed) || signed == 0m)
            {
                error = $"bad or zero amount '{text}'";
                return false;
            }

            var isDebit = signed < 0 ^ layout.PositiveIsDebit;
            direction = isDebit ? Direction.Debit : Direction.Credit;
            amount = Math.Abs(signed);
            return true;
        }

        var debitText = columns.Cell(cells, layout.DebitColumn!);
        var creditText = columns.Cell(cells, layout.CreditColumn!);
        var hasDebit = Money.TryParse(debitText, out var debit) && debit != 0m;
        var hasCredit = Money.TryParse(creditText, out var credit) && credit != 0m;
        var debitGarbage = debitText.Length > 0 && !Money.TryParse(debitText, out _);
        var creditGarbage = creditText.Length > 0 && !Money.TryParse(creditText, out _);

        if (debitGarbage || creditGarbage || hasDebit == hasCredit)
        {
            error = $"need exactly one of debit '{debitText}' / credit '{creditText}'";
            return false;
        }

        direction = hasDebit ? Direction.Debit : Direction.Credit;
        amount = Math.Abs(hasDebit ? debit : credit);
        return true;
    }

    private static StatementParseResult Held(string fileName, string reason, string error) => new()
    {
        File = fileName,
        HoldReason = reason,
        Errors = [error],
    };

    /// <summary>Header name → column index (trimmed, case-insensitive, first occurrence wins).</summary>
    private sealed class Columns
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

        public Columns(IReadOnlyList<string> header)
        {
            for (var i = 0; i < header.Count; i++)
            {
                _index.TryAdd(header[i], i);
            }
        }

        public string Cell(string[] row, string column) =>
            _index.TryGetValue(column.Trim(), out var i) && i < row.Length ? row[i].Trim() : "";

        public IReadOnlyList<string> MissingRequired(CsvLayout layout)
        {
            var required = new List<string?> { layout.DateColumn, layout.DescriptionColumn };
            if (layout.AmountColumn is not null)
            {
                required.Add(layout.AmountColumn);
            }
            else
            {
                required.Add(layout.DebitColumn ?? "(DebitColumn not configured)");
                required.Add(layout.CreditColumn ?? "(CreditColumn not configured)");
            }

            required.AddRange([layout.CheckNoColumn, layout.BalanceColumn, layout.Last4Column]);
            return required
                .OfType<string>()
                .Where(c => !_index.ContainsKey(c.Trim()))
                .ToList();
        }
    }
}
