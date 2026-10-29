using System.Text.Json;
using System.Text.Json.Nodes;

namespace QbAutopost.Core.Extraction;

/// <summary>Builds version 1 of an uploaded request (spec §7.2 item 2).</summary>
public static class UploadVersionBuilder
{
    public static JsonObject Version1(JsonObject extractionPayload, JsonElement body)
    {
        var transactions = extractionPayload["transactions"];
        return new JsonObject
        {
            ["reference"] = Copy(body, "reference"),
            ["controlTotal"] = Copy(body, "controlTotal"),
            ["transactions"] = transactions?.DeepClone(),
        };
    }

    private static JsonNode? Copy(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(v.GetRawText())
            : null;
}
