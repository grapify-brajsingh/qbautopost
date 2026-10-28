using System.Text.Json;

namespace QbAutopost.Core.SavedRequests;

/// <summary>The denormalised columns every store writes from a version's payload, so the fake and EF agree.</summary>
public static class SavedRequestColumns
{
    /// <summary>The largest magnitude <c>control_total numeric(18,2)</c> holds.</summary>
    public const decimal MaxControlTotal = 9_999_999_999_999_999.99m;

    /// <summary>
    /// R33: the <c>control_total</c> column for <paramref name="read"/>. A control total the column cannot hold once
    /// PostgreSQL rounds it to two places (half away from zero) is NULL: the save must not fail for it, the payload
    /// keeps the caller's number, and validation already reports it as a mismatch (CLAUDE.md rule 3: nothing here
    /// changes or recomputes money, it only declines to copy it).
    /// </summary>
    public static decimal? ControlTotalOf(PayloadRead read) =>
        read.Request?.ControlTotal is { } total
        && Math.Abs(decimal.Round(total, 2, MidpointRounding.AwayFromZero)) <= MaxControlTotal
            ? total
            : null;

    /// <summary>
    /// The number of rows marked with a literal <c>post: false</c> (spec 4.1). The strings "false" and numbers do not
    /// count; they are read errors elsewhere.
    /// </summary>
    public static int SkipCountOf(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("transactions", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var count = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object
                && row.TryGetProperty("post", out var post)
                && post.ValueKind == JsonValueKind.False)
            {
                count++;
            }
        }

        return count;
    }
}
