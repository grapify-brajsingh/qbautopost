using System.Text.RegularExpressions;

namespace QbAutopost.Core.Hermes;

/// <summary>Fills <c>{{name}}</c> placeholders in a prompt file (spec §9). A placeholder without a value is an error.</summary>
public static partial class PromptTemplate
{
    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m =>
            values.TryGetValue(m.Groups["name"].Value, out var value)
                ? value
                : throw new InvalidOperationException($"Prompt placeholder {{{{{m.Groups["name"].Value}}}}} has no value."));

    [GeneratedRegex(@"\{\{(?<name>[A-Za-z0-9_]+)\}\}")]
    private static partial Regex Placeholder();
}
