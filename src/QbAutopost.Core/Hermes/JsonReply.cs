using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Hermes;

/// <summary>Turns model reply text into a validated answer (spec §9: strip code fences → deserialise → validate).</summary>
public static class JsonReply
{
    private const string Fence = "```";

    /// <summary>Removes a surrounding Markdown code fence (with or without a language tag); other text is left alone.</summary>
    public static string StripFences(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith(Fence, StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text.Trim('`').Trim();
        }

        var body = text[(firstNewline + 1)..];
        var closing = body.LastIndexOf(Fence, StringComparison.Ordinal);
        return (closing >= 0 ? body[..closing] : body).Trim();
    }

    /// <summary>Returns the answer, or null with the reasons it was rejected.</summary>
    public static T? TryParse<T>(string? content, out IReadOnlyList<string> errors)
        where T : IValidatable
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            errors = ["The reply was empty; expected a JSON object."];
            return default;
        }

        T? answer;
        try
        {
            answer = JsonSerializer.Deserialize<T>(StripFences(content), JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            errors = [$"The reply is not valid JSON for the expected shape: {ex.Message}"];
            return default;
        }

        if (answer is null)
        {
            errors = ["The reply was JSON null; expected a JSON object."];
            return default;
        }

        errors = answer.Validate();
        return errors.Count == 0 ? answer : default;
    }
}
