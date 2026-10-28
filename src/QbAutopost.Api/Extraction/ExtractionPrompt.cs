using System.Text.Json;
using System.Text.Json.Nodes;

namespace QbAutopost.Api.Extraction;

/// <summary>
/// Dashboard-api-m12 6.3: the upload's system prompt and JSON schema, with their version, from the one embedded file
/// <c>extraction-prompt.json</c>. The version is not configuration, so it cannot drift from the text; a test pins the
/// file's SHA-256 together with <see cref="PromptVersion"/>.
/// </summary>
public sealed class ExtractionPrompt
{
    public const string ResourceName = "QbAutopost.Api.Extraction.extraction-prompt.json";

    private ExtractionPrompt(string promptVersion, string systemPrompt, JsonObject schema)
    {
        PromptVersion = promptVersion;
        SystemPrompt = systemPrompt;
        _schema = schema;
    }

    private readonly JsonObject _schema;

    public string PromptVersion { get; }

    public string SystemPrompt { get; }

    /// <summary>A fresh copy each time, so no caller can change what the next request sends.</summary>
    public JsonObject Schema => (JsonObject)_schema.DeepClone();

    /// <summary>The file exactly as embedded.</summary>
    public static byte[] RawBytes()
    {
        using var stream = typeof(ExtractionPrompt).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing from the build.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static ExtractionPrompt Load()
    {
        var root = JsonNode.Parse(RawBytes()) as JsonObject
            ?? throw new InvalidOperationException($"{ResourceName} is not a JSON object.");
        var version = root["promptVersion"]?.GetValue<string>();
        var prompt = root["systemPrompt"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(prompt) || root["schema"] is not JsonObject schema)
        {
            throw new InvalidOperationException($"{ResourceName} needs promptVersion, systemPrompt and schema.");
        }

        return new ExtractionPrompt(version, prompt, (JsonObject)schema.DeepClone());
    }
}
