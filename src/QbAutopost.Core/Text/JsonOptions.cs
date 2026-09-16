using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QbAutopost.Core.Text;

/// <summary>The one JSON configuration used for every file the app reads or writes.</summary>
public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        // Output files are local JSON, never embedded in HTML; keep "&" and quotes readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Enums are written camelCase ("ready", "post", "ccCharge") as in spec §6/§10; reading ignores case.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
