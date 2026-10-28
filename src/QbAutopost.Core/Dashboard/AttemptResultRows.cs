using System.Text.Json;

namespace QbAutopost.Core.Dashboard;

/// <summary>Reads what an attempt's stored result says reached QuickBooks (spec dashboard-api-m12 4.2).</summary>
public static class AttemptResultRows
{
    /// <summary>
    /// The number of entries in <c>posted[]</c> and the <see cref="decimal"/> sum of their <c>amount</c>, never through
    /// <see cref="double"/> and never rounded. <c>(0, 0m)</c> when there is no result, no <c>posted[]</c>, or the text is
    /// not JSON. An entry without a numeric amount counts as a row and adds nothing.
    /// </summary>
    public static (int Rows, decimal Amount) Read(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return (0, 0m);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(resultJson); }
        catch (JsonException) { return (0, 0m); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || Property(doc.RootElement, "posted") is not { ValueKind: JsonValueKind.Array } posted)
                return (0, 0m);

            var rows = 0;
            var amount = 0m;
            foreach (var row in posted.EnumerateArray())
            {
                rows++;
                if (row.ValueKind == JsonValueKind.Object
                    && Property(row, "amount") is { ValueKind: JsonValueKind.Number } a
                    && a.TryGetDecimal(out var value))
                    amount += value;
            }
            return (rows, amount);
        }
    }

    // Stored camelCase (the host's JSON options); matched without case, as JsonOptions.Default reads.
    private static JsonElement? Property(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var exact)) return exact;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
}
