using System.Globalization;

namespace QbAutopost.Core.Gates;

/// <summary>
/// FR-11: refuse to post when <c>QuickBooks:BackupFolder</c> is set and its newest <c>.QBB</c> is older than
/// <c>BackupMaxAgeHours</c>. Returns null when posting may go ahead, otherwise the reason (starts with
/// <c>backup-too-old</c>).
/// </summary>
public static class BackupGuard
{
    public const string Reason = Models.HoldReasons.BackupTooOld;

    public static string? Check(string? backupFolder, int maxAgeHours, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(backupFolder))
        {
            return null;
        }

        // SPEC-GAP T-604: a missing folder or a folder without backups counts as "too old" (post less).
        if (!Directory.Exists(backupFolder))
        {
            return $"{Reason}: backup folder '{backupFolder}' does not exist";
        }

        var newest = new DirectoryInfo(backupFolder)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => string.Equals(f.Extension, ".qbb", StringComparison.OrdinalIgnoreCase))
            .MaxBy(f => f.LastWriteTimeUtc);
        if (newest is null)
        {
            return $"{Reason}: no .QBB backup in '{backupFolder}'";
        }

        var age = utcNow - newest.LastWriteTimeUtc;
        return age > TimeSpan.FromHours(maxAgeHours)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{Reason}: newest backup '{newest.Name}' is {age.TotalHours:0.#} h old (limit {maxAgeHours} h)")
            : null;
    }
}
