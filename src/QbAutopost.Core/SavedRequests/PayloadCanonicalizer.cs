using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.SavedRequests;

/// <summary>
/// Canonical form of a validate-and-post payload (spec §9): keys sorted ordinally, no insignificant whitespace,
/// money fields (<c>controlTotal</c>, <c>transactions[*].amount</c>) as invariant <c>0.00</c>, strings and every
/// other number unchanged (raw token).
/// </summary>
public static class PayloadCanonicalizer
{
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Canonicalize(JsonElement payload, PayloadRules rules = PayloadRules.V1) =>
        Normalize(payload, rules).ToJsonString(WriteOptions);

    public static string Sha256(JsonElement payload, PayloadRules rules = PayloadRules.V1) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(payload, rules)))).ToLowerInvariant();

    /// <summary>The canonical form as a tree, shared with <see cref="PayloadDiff"/>.</summary>
    internal static JsonNode Normalize(JsonElement payload, PayloadRules rules = PayloadRules.V1) => Walk(payload, "$", rules);

    private static JsonNode Walk(JsonElement e, string path, PayloadRules rules)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (rules == PayloadRules.V2 && IsDroppedByV2(path, p))
                    {
                        continue;
                    }

                    obj[p.Name] = Walk(p.Value, path + "." + p.Name, rules);
                }

                return obj;
            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in e.EnumerateArray())
                {
                    arr.Add(Walk(item, path + "[*]", rules));
                }

                return arr;
            case JsonValueKind.Number:
                return IsMoneyPath(path) ? MoneyOrRaw(e) : JsonNode.Parse(e.GetRawText())!;
            default:
                return JsonNode.Parse(e.GetRawText())!;
        }
    }

    // V2 (spec section 7): top-level email and pushId are not part of the request; a row's post is dropped only when it is
    // literally true (a direct child of a transactions row, never a nested post elsewhere).
    private static bool IsDroppedByV2(string path, JsonProperty p) =>
        (path == "$" && p.Name is "email" or "pushId")
        || (path == "$.transactions[*]" && p.Name == "post" && p.Value.ValueKind == JsonValueKind.True);

    private static bool IsMoneyPath(string path) => path is "$.controlTotal" or "$.transactions[*].amount";

    // A value that does not fit decimal, or that has more than two places, keeps its raw token:
    // rounding money to make two payloads look equal is exactly what the hash must not do.
    private static JsonNode MoneyOrRaw(JsonElement e) =>
        e.TryGetDecimal(out var d) && decimal.Round(d, 2) == d
            ? JsonNode.Parse(Money.Format(d))!
            : JsonNode.Parse(e.GetRawText())!;
}
