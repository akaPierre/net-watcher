using System.Windows.Threading;
using NetWatcher.Models;
using NetWatcher.Services;

namespace NetWatcher.ViewModels;

/// <summary>Housekeeping: trims old history and takes a weekly backup of the database.</summary>
public partial class MainViewModel
{
    private readonly DispatcherTimer _maintenanceTimer = new() { Interval = TimeSpan.FromHours(6) };

    private void StartMaintenance()
    {
        _maintenanceTimer.Tick += (_, _) => RunMaintenance(force: false);
        _maintenanceTimer.Start();
        // First run shortly after startup, so it never slows down opening the app.
        var once = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        once.Tick += (_, _) => { once.Stop(); RunMaintenance(force: false); };
        once.Start();
    }

    /// <summary>Prunes old rows and backs up the database if a backup is due (or forced).</summary>
    public string? RunMaintenance(bool force)
    {
        try
        {
            int removed = _db.Prune(Settings.EventRetentionDays, Settings.HealthRetentionDays);
            var cutoff = DateTime.Now.AddDays(-Settings.EventRetentionDays);
            for (int i = Events.Count - 1; i >= 0 && Events[i].Time < cutoff; i--) Events.RemoveAt(i);

            string? backup = null;
            if (force || Maintenance.BackupDue())
            {
                backup = Maintenance.Backup(_db, Settings.BackupsToKeep);
                AddEvent(EventKind.Maintenance, null, null, Loc.T("Msg.BackupSaved", backup));
            }
            if (removed > 0) AddEvent(EventKind.Maintenance, null, null, Loc.T("Msg.Pruned", removed));
            return backup;
        }
        catch (Exception ex)
        {
            AddEvent(EventKind.Maintenance, null, null, Loc.T("Msg.MaintenanceFailed", ex.Message));
            if (force) throw;
            return null;
        }
    }
}
