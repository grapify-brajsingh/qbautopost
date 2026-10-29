using System.Text.Json;
using System.Text.Json.Serialization;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Core.Extraction;

/// <summary>
/// The model's reply to the statement-extraction prompt (spec §6.3, Appendix A). Rows stay raw
/// <see cref="JsonElement"/>s: <see cref="ExtractionTidy"/> reads them so amounts never pass through a double.
/// </summary>
public sealed class ExtractionAnswer : IValidatable
{
    [JsonPropertyName("is_bank_statement")]
    public bool? IsBankStatement { get; init; }

    public string? Reference { get; init; }

    public List<JsonElement>? Transactions { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (IsBankStatement is null)
        {
            errors.Add("is_bank_statement is required (true or false).");
        }

        if (Transactions is null)
        {
            errors.Add("transactions is required and must be an array (use [] when there are none).");
            return errors;
        }

        for (var i = 0; i < Transactions.Count; i++)
        {
            if (Transactions[i].ValueKind != JsonValueKind.Object)
            {
                errors.Add($"transactions[{i}] is not an object; every transaction must be an object.");
            }
        }

        return errors;
    }
}
