using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TOLIK.Core.Host;

/// <summary>
/// Schedules PostgreSQL backups (docs/backup-recovery.md gap: "No schedule").
/// Runs the existing backup script (pg_dump custom format + integrity check + SHA-256
/// sidecar) once per interval (default 24h, 0 = disabled) and prunes old dumps
/// (retention: keep last 7). Works on the host deployment (the script shells out to
/// docker); in a container the script fails and the run is logged + skipped — the
/// container deployment backs up from the host side (§40: report, never pretend).
/// Failures never crash the host.
/// </summary>
public sealed class BackupSchedulerBackgroundService(
    IConfiguration configuration,
    ILogger<BackupSchedulerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue("TOLIK_BACKUP_INTERVAL_HOURS", 24);
        if (intervalHours <= 0)
        {
            logger.LogInformation("Scheduled backups disabled (TOLIK_BACKUP_INTERVAL_HOURS={Interval}).", intervalHours);
            return;
        }
        var backupDir = configuration["TOLIK_BACKUP_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "backups");
        var keep = configuration.GetValue("TOLIK_BACKUP_KEEP", 7);

        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        var lastBackup = (DateTimeOffset?)null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                if (lastBackup is null || now - lastBackup.Value >= TimeSpan.FromHours(intervalHours))
                {
                    lastBackup = now;
                    await RunBackupAsync(backupDir, keep, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled backup failed; retrying on next tick.");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Runs the existing backup script into the target directory, then prunes old dumps.
    /// The script is validated (pg_restore --list + SHA-256 sidecar) by design.
    /// </summary>
    private async Task RunBackupAsync(string backupDir, int keep, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDir);
        var script = Path.GetFullPath(Path.Combine("scripts", "backup-postgres.ps1"));
        if (!File.Exists(script))
        {
            logger.LogWarning("Backup script not found at {Script}; scheduled backup skipped (host deployment backs up from the host side).", script);
            return;
        }
        var target = Path.Combine(backupDir, $"tolik-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.dump");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -BackupPath \"{target}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            logger.LogWarning("Backup process failed to start; scheduled backup skipped.");
            return;
        }
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            logger.LogWarning("Scheduled backup failed (exit {Code}): {Error}", process.ExitCode, stderr);
            return;
        }
        var removed = PruneBackups(backupDir, keep);
        logger.LogInformation("Scheduled backup completed: {Target} (retention pruned {Removed} old dumps).", target, removed);
    }

    /// <summary>
    /// Retention (docs/backup-recovery.md): keep the last `keep` dumps; older dumps and
    /// their SHA-256 sidecars are deleted. Static for direct unit testing.
    /// </summary>
    public static int PruneBackups(string directory, int keep)
    {
        if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep));
        if (!Directory.Exists(directory)) return 0;
        var dumps = Directory.GetFiles(directory, "tolik-backup-*.dump")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();
        var removed = 0;
        foreach (var old in dumps.Skip(keep))
        {
            var sidecar = old.FullName + ".sha256";
            if (File.Exists(sidecar)) File.Delete(sidecar);
            old.Delete();
            removed++;
        }
        return removed;
    }
}
