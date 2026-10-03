namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Backup retention (docs/backup-recovery.md): keep the last `keep` dumps; older dumps
/// and their SHA-256 sidecars are deleted. The scheduler shells out to the validated
/// backup script once per interval (default 24h, 0 = disabled).
/// </summary>
public sealed class BackupRetentionTests
{
    private static string CreateDump(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "dump-content");
        File.WriteAllText(path + ".sha256", "hash");
        return path;
    }

    [Fact]
    public void Retention_keeps_last_seven_and_deletes_sidecars()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tolik-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var path = CreateDump(dir, $"tolik-backup-2026100{i % 10}-0000.dump");
                File.SetCreationTimeUtc(path, DateTimeOffset.UtcNow.AddDays(-i).UtcDateTime);
            }
            var removed = BackupSchedulerBackgroundService.PruneBackups(dir, keep: 7);
            Assert.Equal(3, removed);
            Assert.Equal(7, Directory.GetFiles(dir, "tolik-backup-*.dump").Length);
            // One sidecar per kept dump; the removed dumps' sidecars are gone.
            Assert.Equal(7, Directory.GetFiles(dir, "*.sha256").Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Fewer_dumps_than_keep_prunes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tolik-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            CreateDump(dir, "tolik-backup-20261001-0000.dump");
            Assert.Equal(0, BackupSchedulerBackgroundService.PruneBackups(dir, keep: 7));
            Assert.Single(Directory.GetFiles(dir, "tolik-backup-*.dump"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Missing_directory_prunes_nothing_and_keep_below_one_is_rejected()
    {
        Assert.Equal(0, BackupSchedulerBackgroundService.PruneBackups(Path.Combine(Path.GetTempPath(), "tolik-missing-" + Guid.NewGuid().ToString("N")), 7));
        var dir = Path.Combine(Path.GetTempPath(), "tolik-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.Throws<ArgumentOutOfRangeException>(() => BackupSchedulerBackgroundService.PruneBackups(dir, keep: 0)); }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
