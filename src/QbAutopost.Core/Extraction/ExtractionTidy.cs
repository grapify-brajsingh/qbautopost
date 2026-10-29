using System.Text.Json;
using System.Text.Json.Nodes;

namespace QbAutopost.Core.Extraction;

/// <summary>
/// Turns the model's parsed reply into the response payload (spec §6.4). Writes only what the model returned:
/// no defaults, no sums. <c>controlTotal</c> is always null (rule 3). Amounts are read as decimal.
/// </summary>
public static class ExtractionTidy
{
    private static readonly string[] TextFields = ["date", "account", "lineAccount", "memo", "last4", "payee", "checkNo"];

    public static JsonObject Tidy(ExtractionAnswer answer)
    {
        var rows = new JsonArray();
        var number = 0;
        foreach (var item in answer.Transactions ?? [])
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            number++;
            rows.Add(TidyRow(item, number));
        }

        return new JsonObject
        {
            ["reference"] = Clean(answer.Reference),
            ["controlTotal"] = null,
            ["transactions"] = rows,
        };
    }

    private static JsonObject TidyRow(JsonElement item, int number)
    {
        var row = new JsonObject
        {
            ["externalId"] = $"row-{number}",
            ["kind"] = IsDeposit(item) ? "Deposit" : "Check",
        };

        foreach (var field in TextFields)
        {
            if (Clean(Text(item, field)) is { } value)
            {
                row[field] = value;
            }
        }

        if (item.TryGetProperty("amount", out var amount)
            && amount.ValueKind == JsonValueKind.Number
            && amount.TryGetDecimal(out var value2))
        {
            row["amount"] = Math.Abs(value2);
        }

        return row;
    }

    private static bool IsDeposit(JsonElement item) =>
        string.Equals(Text(item, "kind")?.Trim(), "deposit", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
