using Microsoft.EntityFrameworkCore;

namespace QbAutopost.Data;

/// <param name="Pending">Migrations this build carries that the database has not applied.</param>
/// <param name="Reason"><c>unreachable</c> | <c>schema-out-of-date</c>; null when current.</param>
public sealed record SchemaStatus(bool Reachable, bool Current, IReadOnlyList<string> Pending, string? Reason);

/// <summary>Compares the database's applied migrations with the ones this build carries (spec §5.5). Never migrates.</summary>
public static class SchemaState
{
    public const string Unreachable = "unreachable";
    public const string OutOfDate = "schema-out-of-date";

    public static async Task<SchemaStatus> CheckAsync(QbAutopostDbContext db, CancellationToken ct)
    {
        List<string> applied;
        try
        {
            if (!await db.Database.CanConnectAsync(ct))
            {
                return new SchemaStatus(false, false, [], Unreachable);
            }

            applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception text can name the host; the caller reports the reason only.
            return new SchemaStatus(false, false, [], Unreachable);
        }

        var known = db.Database.GetMigrations().ToList();
        var pending = known.Except(applied, StringComparer.Ordinal).ToList();
        // A database migrated by a newer build is not current for this one either: "if they differ" (§5.5).
        var unknownApplied = applied.Except(known, StringComparer.Ordinal).Any();

        return pending.Count == 0 && !unknownApplied
            ? new SchemaStatus(true, true, [], null)
            : new SchemaStatus(true, false, pending, OutOfDate);
    }
}
