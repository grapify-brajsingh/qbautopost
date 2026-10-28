using System.Text.Json;
using System.Text.Json.Nodes;

namespace QbAutopost.Core.SavedRequests;

/// <summary>
/// Dashboard v2 spec 7.1: gives object rows without an <c>externalId</c> a unique one. Works on a copy; rows that
/// have an id, non-object rows and a non-array <c>transactions</c> are left alone, and duplicate caller ids stay.
/// </summary>
public static class ExternalIdAssigner
{
    public static JsonElement AssignOnSave(JsonElement payload) => Assign(payload, n => $"row-{n}");

    public static JsonElement AssignOnEdit(JsonElement payload, int newVersion) =>
        Assign(payload, n => $"edit{newVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{n}");

    private static JsonElement Assign(JsonElement payload, Func<int, string> baseId)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("transactions", out var probe)
            || probe.ValueKind != JsonValueKind.Array)
        {
            return payload;
        }

        try
        {
            return AssignNodes(payload, baseId);
        }
        catch (ArgumentException)
        {
            // JsonNode rejects duplicate property names (lazily, on first access) that JsonDocument accepts:
            // leave the payload as it is.
            return payload;
        }
    }

    private static JsonElement AssignNodes(JsonElement payload, Func<int, string> baseId)
    {
        var root = JsonNode.Parse(payload.GetRawText())!;
        var rows = root["transactions"]!.AsArray();

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is JsonObject o && o["externalId"] is JsonValue v && v.TryGetValue<string>(out var id))
            {
                used.Add(id);
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject obj || !IsAbsent(obj["externalId"]))
            {
                continue;
            }

            var candidate = baseId(i + 1);
            for (var suffix = 2; used.Contains(candidate); suffix++)
            {
                candidate = $"{baseId(i + 1)}-{suffix}";
            }

            used.Add(candidate);
            obj["externalId"] = candidate;
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static bool IsAbsent(JsonNode? node) =>
        node is null || (node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0)
        || node.GetValueKind() == JsonValueKind.Null;
}
