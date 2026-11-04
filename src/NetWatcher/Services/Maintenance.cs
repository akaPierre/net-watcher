using System.Globalization;
using System.IO;

namespace NetWatcher.Services;

/// <summary>Weekly database backups and pruning of old history.</summary>
public static class Maintenance
{
    public static readonly TimeSpan BackupEvery = TimeSpan.FromDays(7);

    public static string BackupDir => Path.Combine(AppPaths.DataDir, "backups");

    public static DateTime? LastBackup()
    {
        if (!Directory.Exists(BackupDir)) return null;
        return Directory.GetFiles(BackupDir, "netwatcher-*.db")
            .Select(File.GetLastWriteTime)
            .DefaultIfEmpty()
            .Max() is { } t && t != default ? t : null;
    }

    public static bool BackupDue() => LastBackup() is not { } last || DateTime.Now - last >= BackupEvery;

    /// <summary>Copies the live database to backups\netwatcher-YYYY-MM-DD_HHmm.db and keeps only the newest N.</summary>
    public static string Backup(Database db, int keep)
    {
        Directory.CreateDirectory(BackupDir);
        var path = Path.Combine(BackupDir,
            $"netwatcher-{DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture)}.db");
        db.BackupTo(path);

        foreach (var old in Directory.GetFiles(BackupDir, "netwatcher-*.db")
                     .OrderByDescending(File.GetLastWriteTime)
                     .Skip(Math.Max(1, keep)))
        {
            try { File.Delete(old); } catch { /* in use; try next time */ }
        }
        return path;
    }
}
