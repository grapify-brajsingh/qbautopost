using System.Text.Json;
using System.Text.Json.Nodes;

namespace QbAutopost.Core.SavedRequests;

/// <param name="Op"><c>add</c>, <c>remove</c> or <c>replace</c>.</param>
public sealed record PayloadChange(string Op, string Path, JsonNode? Old, JsonNode? New);

/// <summary>
/// Change list between two payloads (spec §8.3), computed on their canonical forms so key order, whitespace and
/// <c>184.3</c> vs <c>184.30</c> are not changes. <c>transactions</c> rows are matched by <c>externalId</c> when
/// every row in both payloads has a distinct one, by index otherwise.
/// </summary>
public static class PayloadDiff
{
    private const string TransactionsPath = "$.transactions";

    public static IReadOnlyList<PayloadChange> Compute(JsonElement? previous, JsonElement next, PayloadRules rules = PayloadRules.V1)
    {
        if (previous is null)
        {
            return [];
        }

        var changes = new List<PayloadChange>();
        Diff("$", PayloadCanonicalizer.Normalize(previous.Value, rules), PayloadCanonicalizer.Normalize(next, rules), changes, rules);
        return changes;
    }

    private static void Diff(string path, JsonNode? old, JsonNode? next, List<PayloadChange> changes, PayloadRules rules)
    {
        if (Same(old, next))
        {
            return;
        }

        switch (old, next)
        {
            case (JsonObject o, JsonObject n):
                DiffObject(path, o, n, changes, rules);
                break;
            case (JsonArray o, JsonArray n):
                if (path == TransactionsPath && Keys(o) is { } oldKeys && Keys(n) is { } newKeys)
                {
                    DiffKeyed(path, o, n, oldKeys, newKeys, changes, rules);
                }
                else
                {
                    DiffByIndex(path, o, n, changes, rules);
                }

                break;
            default:
                changes.Add(new PayloadChange("replace", path, Clone(old), Clone(next)));
                break;
        }
    }

    private static void DiffObject(string path, JsonObject o, JsonObject n, List<PayloadChange> changes, PayloadRules rules)
    {
        var keys = o.Select(p => p.Key).Union(n.Select(p => p.Key)).OrderBy(k => k, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var childPath = PropertyPath(path, key);
            var inOld = o.ContainsKey(key);
            var inNew = n.ContainsKey(key);
            if (inOld && inNew)
            {
                Diff(childPath, o[key], n[key], changes, rules);
            }
            else if (inNew)
            {
                changes.Add(new PayloadChange("add", childPath, null, Clone(n[key])));
            }
            else
            {
                changes.Add(new PayloadChange("remove", childPath, Clone(o[key]), null));
            }
        }
    }

    private static void DiffByIndex(string path, JsonArray o, JsonArray n, List<PayloadChange> changes, PayloadRules rules)
    {
        for (var i = 0; i < n.Count; i++)
        {
            if (i < o.Count)
            {
                DiffMatched(path, $"{path}[{i}]", o[i], n[i], changes, rules);
            }
            else
            {
                changes.Add(new PayloadChange("add", $"{path}[{i}]", null, Clone(n[i])));
            }
        }

        for (var i = n.Count; i < o.Count; i++)
        {
            changes.Add(new PayloadChange("remove", $"{path}[{i}]", Clone(o[i]), null));
        }
    }

    // New-payload order for add/replace (new index), then old-payload order for remove (old index).
    private static void DiffKeyed(string path, JsonArray o, JsonArray n, string[] oldKeys, string[] newKeys, List<PayloadChange> changes, PayloadRules rules)
    {
        var oldIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < oldKeys.Length; i++)
        {
            oldIndex[oldKeys[i]] = i;
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < n.Count; i++)
        {
            if (oldIndex.TryGetValue(newKeys[i], out var oi))
            {
                matched.Add(newKeys[i]);
                DiffMatched(path, $"{path}[{i}]", o[oi], n[i], changes, rules);
            }
            else
            {
                changes.Add(new PayloadChange("add", $"{path}[{i}]", null, Clone(n[i])));
            }
        }

        for (var i = 0; i < o.Count; i++)
        {
            if (!matched.Contains(oldKeys[i]))
            {
                changes.Add(new PayloadChange("remove", $"{path}[{i}]", Clone(o[i]), null));
            }
        }
    }

    // Under V2 the hash form drops post:true, so for two matched rows a missing post means true (R2). Only this
    // comparison injects it: whole-row add/remove report the row as stored.
    private static void DiffMatched(string arrayPath, string path, JsonNode? old, JsonNode? next, List<PayloadChange> changes, PayloadRules rules)
    {
        if (rules == PayloadRules.V2 && arrayPath == TransactionsPath && old is JsonObject o && next is JsonObject n)
        {
            old = WithDefaultPost(o);
            next = WithDefaultPost(n);
        }

        Diff(path, old, next, changes, rules);
    }

    private static JsonObject WithDefaultPost(JsonObject row)
    {
        var copy = (JsonObject)row.DeepClone();
        if (!copy.ContainsKey("post"))
        {
            copy["post"] = true;
        }

        return copy;
    }

    // Null unless every element is an object with a non-null externalId and no id repeats.
    private static string[]? Keys(JsonArray rows)
    {
        var keys = new string[rows.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject row || row["externalId"] is not { } id)
            {
                return null;
            }

            keys[i] = id.ToJsonString();
            if (!seen.Add(keys[i]))
            {
                return null;
            }
        }

        return keys;
    }

    private static bool Same(JsonNode? a, JsonNode? b) =>
        string.Equals(a?.ToJsonString(), b?.ToJsonString(), StringComparison.Ordinal);

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    private static string PropertyPath(string parent, string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? $"{parent}.{name}"
            : $"{parent}['{name.Replace("'", "\\'", StringComparison.Ordinal)}']";
}
