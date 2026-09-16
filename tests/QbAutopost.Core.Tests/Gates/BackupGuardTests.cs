using QbAutopost.Core.Gates;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Gates;

/// <summary>FR-11 backup-age guard.</summary>
public sealed class BackupGuardTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempJobFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Backups => _dir.Folder;

    private void Backup(string name, double hoursOld)
    {
        var path = Path.Combine(Backups, name);
        File.WriteAllText(path, "qbb");
        File.SetLastWriteTimeUtc(path, Now.AddHours(-hoursOld));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Should_Allow_When_NoBackupFolderIsConfigured(string? folder)
    {
        Assert.Null(BackupGuard.Check(folder, 36, Now));
    }

    [Fact]
    public void Should_Allow_When_NewestBackupIsWithinTheLimit()
    {
        Backup("old.QBB", 100);
        Backup("new.qbb", 35.9);

        Assert.Null(BackupGuard.Check(Backups, 36, Now));
    }

    [Fact]
    public void Should_Refuse_When_NewestBackupIsTooOld()
    {
        Backup("Tropicana.QBB", 36.5);

        var reason = BackupGuard.Check(Backups, 36, Now);

        Assert.StartsWith("backup-too-old: newest backup 'Tropicana.QBB' is 36.5 h old (limit 36 h)", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_IgnoreOtherFiles_When_FindingTheNewestBackup()
    {
        Backup("Tropicana.QBB", 50);
        Backup("notes.txt", 1);
        Backup("Tropicana.QBW", 1);

        Assert.StartsWith(BackupGuard.Reason, BackupGuard.Check(Backups, 36, Now), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Allow_When_NewestBackupIsExactlyAtTheLimit()
    {
        Backup("Tropicana.QBB", 36);

        Assert.Null(BackupGuard.Check(Backups, 36, Now));
    }

    [Fact]
    public void Should_UseTheGivenLimit_When_ItIsNotTheDefault()
    {
        Backup("Tropicana.QBB", 40);

        Assert.Null(BackupGuard.Check(Backups, 48, Now));
        Assert.NotNull(BackupGuard.Check(Backups, 24, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Should_RefuseEveryPastBackup_When_LimitIsZeroOrLess(int maxAgeHours)
    {
        Backup("Tropicana.QBB", 0.01);

        Assert.StartsWith(BackupGuard.Reason, BackupGuard.Check(Backups, maxAgeHours, Now), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Refuse_When_FolderHasNoBackup()
    {
        Assert.StartsWith("backup-too-old: no .QBB backup", BackupGuard.Check(Backups, 36, Now), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Refuse_When_FolderDoesNotExist()
    {
        var missing = Path.Combine(Backups, "missing");

        Assert.StartsWith("backup-too-old: backup folder", BackupGuard.Check(missing, 36, Now), StringComparison.Ordinal);
    }
}
