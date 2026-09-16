using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Mapping;

/// <summary>Which alias table <c>POST /rules/alias</c> writes (FR-14).</summary>
public enum AliasKind
{
    Vendor,
    Customer,
}

/// <summary>One rule written to <c>rules.json</c>; <paramref name="Previous"/> is the value it replaced, if any.</summary>
public sealed record RuleChange(string Section, string Key, string Value, string? Previous);

/// <summary>A taught rule that was refused (blank value, or a name QuickBooks does not have). Nothing was written.</summary>
public sealed class RuleValidationException(string message) : Exception(message);

/// <summary>
/// FR-14 rules teaching: writes <c>PayeeAliases</c> / <c>CustomerAliases</c> / <c>VendorAccounts</c> in
/// <c>rules.json</c>. Jobs load the file once per run, so a change applies from the next job or re-post (spec §11).
/// The file is rewritten atomically, one writer at a time, and only when both the old and new content load as rules.
/// SPEC-GAP T-701: comments in <c>rules.json</c> are not kept by a rewrite (JSON has no comment model); names are
/// checked against <c>qb-lists.json</c> when it exists (spec asks this only for the account); fragments need at least
/// <see cref="MinFragmentLength"/> characters so an alias cannot match almost every description.
/// </summary>
public sealed class RulesEditor(string rulesFile, string qbListsFile)
{
    public const int MinFragmentLength = 3;

    public const string PayeeAliasesSection = "PayeeAliases";
    public const string CustomerAliasesSection = "CustomerAliases";
    public const string VendorAccountsSection = "VendorAccounts";

    // One lock for every editor in the process: the host has one rules file, and tests may create several editors.
    private static readonly object WriteLock = new();

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public RuleChange SetAlias(string? fragment, string? name, AliasKind kind)
    {
        var key = TextNormalizer.Normalize(fragment);
        if (key.Length < MinFragmentLength)
        {
            throw new RuleValidationException($"fragment must have at least {MinFragmentLength} characters.");
        }

        var value = Required(name, "name");
        var lists = LoadListsIfSynced();
        if (lists is not null)
        {
            var known = kind == AliasKind.Vendor ? lists.Vendors : lists.Customers;
            RequireListed(value, known, kind == AliasKind.Vendor ? "vendor" : "customer");
        }

        var section = kind == AliasKind.Vendor ? PayeeAliasesSection : CustomerAliasesSection;
        return Write(section, key, value, existing => TextNormalizer.Normalize(existing) == key);
    }

    public RuleChange SetVendorAccount(string? vendor, string? account)
    {
        var key = Required(vendor, "vendor");
        var value = Required(account, "account");
        var lists = LoadListsIfSynced();
        if (lists is not null)
        {
            RequireListed(key, lists.Vendors, "vendor");
            RequireListed(value, lists.Accounts.Select(a => a.Name).ToList(), "account");
        }

        // Rules looks vendors up ignoring case, so a key differing only in case is the same rule.
        return Write(VendorAccountsSection, key, value, existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase));
    }

    private RuleChange Write(string section, string key, string value, Func<string, bool> sameKey)
    {
        lock (WriteLock)
        {
            var original = AtomicFile.ReadAllText(rulesFile);
            Rules.Parse(original); // a broken file is reported, never rewritten

            var root = JsonNode.Parse(original, documentOptions: ReadOptions) as JsonObject
                ?? throw new InvalidDataException("rules.json is not a JSON object.");
            var table = SectionOf(root, section);

            string? previous = null;
            foreach (var existing in table.Select(p => p.Key).Where(sameKey).ToList())
            {
                previous ??= table[existing]?.GetValue<string>();
                table.Remove(existing);
            }

            table[key] = value;
            var updated = root.ToJsonString(WriteOptions);
            Rules.Parse(updated);
            AtomicFile.WriteAllText(rulesFile, updated);
            return new RuleChange(section, key, value, previous);
        }
    }

    /// <summary>The section object, matched ignoring case (JsonOptions reads that way); created or replaced when missing or null.</summary>
    private static JsonObject SectionOf(JsonObject root, string section)
    {
        var name = root.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, section, StringComparison.OrdinalIgnoreCase)) ?? section;
        if (root[name] is JsonObject table)
        {
            return table;
        }

        if (root[name] is not null)
        {
            throw new InvalidDataException($"rules.json {name} is not an object.");
        }

        table = new JsonObject();
        root[name] = table;
        return table;
    }

    private QbLists? LoadListsIfSynced() =>
        File.Exists(qbListsFile) ? new QbListsStore(qbListsFile).Load() : null;

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new RuleValidationException($"{field} is required.")
            : value.Trim();

    /// <summary>QuickBooks names must match exactly (as FR-15 <c>missingInRules</c> compares them).</summary>
    private static void RequireListed(string name, IReadOnlyList<string> known, string what)
    {
        if (known.Contains(name, StringComparer.Ordinal))
        {
            return;
        }

        var close = known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        var hint = close is null ? " Sync lists after adding it in QuickBooks." : $" Did you mean '{close}'?";
        throw new RuleValidationException($"{what} '{name}' is not in qb-lists.json.{hint}");
    }
}
