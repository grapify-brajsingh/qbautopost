using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace QbAutopost.Data;

/// <summary>
/// Stores an enum as kebab-case lower text (<c>in-progress</c>, <c>not-posted</c>), the spelling the API serialises.
/// Unknown text throws rather than being read as a default value.
/// </summary>
public sealed class KebabCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(v => ToText(v), s => FromText(s))
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<TEnum, string> Texts =
        Enum.GetValues<TEnum>().ToFrozenDictionary(v => v, v => JsonNamingPolicy.KebabCaseLower.ConvertName(v.ToString()));

    private static readonly FrozenDictionary<string, TEnum> Values =
        Texts.ToFrozenDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public static string ToText(TEnum value) =>
        Texts.TryGetValue(value, out var text)
            ? text
            : throw new InvalidOperationException($"{typeof(TEnum).Name} has no value {value}.");

    public static TEnum FromText(string text) =>
        Values.TryGetValue(text, out var value)
            ? value
            : throw new InvalidOperationException($"'{text}' is not a stored {typeof(TEnum).Name}.");
}
