using System.Text.Json;

namespace QbAutopost.Core.SavedRequests;

/// <summary>The row indexes marked <c>post: false</c>, and path-named errors for a <c>post</c> that is not a boolean.</summary>
public sealed record NotPostedRead(IReadOnlySet<int> Indexes, IReadOnlyList<string> Errors);

/// <summary>
/// Dashboard v2 §6.1: reads the per-row <c>post</c> flag from a stored payload. <c>DirectRow</c> is untouched, so v1
/// binding (where <c>post</c> is an unknown member and ignored) stays as it is. Missing or <c>true</c> means post.
/// </summary>
public static class NotPostedRows
{
    public static NotPostedRead Read(JsonElement payload)
    {
        var indexes = new HashSet<int>();
        var errors = new List<string>();
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("transactions", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return new NotPostedRead(indexes, errors);
        }

        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("post", out var post))
            {
                switch (post.ValueKind)
                {
                    case JsonValueKind.False:
                        indexes.Add(index);
                        break;
                    case JsonValueKind.True:
                        break;
                    default:
                        errors.Add($"$.transactions[{index}].post: must be true or false");
                        break;
                }
            }

            index++;
        }

        return new NotPostedRead(indexes, errors);
    }
}
