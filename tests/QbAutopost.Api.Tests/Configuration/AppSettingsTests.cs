using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Tests.Configuration;

/// <summary>Spec §12 defaults and path resolution that the FR-11 backup guard relies on.</summary>
public sealed class AppSettingsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "qbautopost-root");

    [Fact]
    public void Should_DefaultToNoBackupCheckAnd36Hours_When_NotConfigured()
    {
        var qb = new AppSettings().QuickBooks;

        Assert.Equal(("", 36), (qb.BackupFolder, qb.BackupMaxAgeHours));
    }

    [Fact]
    public void Should_ResolveRelativeBackupFolder_When_PathsAreResolved()
    {
        var settings = new AppSettings { QuickBooks = { BackupFolder = Path.Combine("backups", "qbb") } };

        settings.ResolvePaths(Root);

        Assert.Equal(Path.Combine(Root, "backups", "qbb"), settings.QuickBooks.BackupFolder);
    }

    [Fact]
    public void Should_KeepAbsoluteBackupFolder_When_PathsAreResolved()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "backups");
        var settings = new AppSettings { QuickBooks = { BackupFolder = absolute } };

        settings.ResolvePaths(Root);

        Assert.Equal(absolute, settings.QuickBooks.BackupFolder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_LeaveBlankBackupFolderBlank_When_PathsAreResolved(string folder)
    {
        var settings = new AppSettings { QuickBooks = { BackupFolder = folder } };

        settings.ResolvePaths(Root);

        Assert.Equal(folder, settings.QuickBooks.BackupFolder);
    }
}
