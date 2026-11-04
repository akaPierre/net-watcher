using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NetWatcher.Services;
using NetWatcher.ViewModels;

namespace NetWatcher;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        var s = vm.Settings;

        StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled();
        CloseToTrayBox.IsChecked = s.CloseToTray;
        foreach (var m in vm.IntervalOptions) ScanIntervalBox.Items.Add(m);
        ScanIntervalBox.SelectedItem = vm.IntervalOptions.Contains(s.AutoScanMinutes) ? s.AutoScanMinutes : 5;

        var interfaces = new List<Tuple<string, string>> { Tuple.Create("", Loc.T("Settings.InterfaceAuto")) };
        interfaces.AddRange(LocalNetwork.ListInterfaces().Select(i => Tuple.Create(i.Name, i.Description)));
        if (s.Interface is { } chosen && interfaces.All(i => i.Item1 != chosen))
            interfaces.Add(Tuple.Create(chosen, chosen + " " + Loc.T("Settings.InterfaceMissing")));
        InterfaceBox.ItemsSource = interfaces;
        InterfaceBox.SelectedValue = s.Interface ?? "";
        RangeBox.Text = s.ScanRange ?? "";

        AlertNewBox.IsChecked = s.AlertNewDevice;
        AlertUnknownBox.IsChecked = s.AlertUnknownOnline;
        AlertConflictBox.IsChecked = s.AlertConflict;
        AlertCriticalBox.IsChecked = s.AlertCritical;
        AlertInternetBox.IsChecked = s.AlertInternet;

        foreach (var sec in AppSettings.HealthIntervalOptions) HealthIntervalBox.Items.Add(sec);
        HealthIntervalBox.SelectedItem = AppSettings.HealthIntervalOptions.Contains(s.HealthIntervalSeconds) ? s.HealthIntervalSeconds : 60;
        TargetsBox.Text = s.InternetTargets;

        EventDaysBox.Text = s.EventRetentionDays.ToString();
        HealthDaysBox.Text = s.HealthRetentionDays.ToString();
        BackupsBox.Text = s.BackupsToKeep.ToString();
        UpdateLastBackup();
    }

    private void UpdateLastBackup() =>
        LastBackupText.Text = Maintenance.LastBackup() is { } t
            ? Loc.T("Settings.LastBackup", t.ToString("g"))
            : Loc.T("Settings.NoBackupYet");

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _vm.Settings.Clone();

        if (!TryDays(EventDaysBox, 7, 3650, out var eventDays) ||
            !TryDays(HealthDaysBox, 7, 3650, out var healthDays) ||
            !TryDays(BackupsBox, 1, 100, out var backups))
            return;

        var range = RangeBox.Text.Trim();
        var iface = InterfaceBox.SelectedValue as string;
        if (range.Length > 0)
        {
            var net = LocalNetwork.Detect(string.IsNullOrEmpty(iface) ? null : iface);
            string? error = net is null ? Loc.T("Status.NoNetwork") : null;
            if (net is not null) LocalNetwork.TryParseRange(net, range, out _, out _, out error);
            if (error is not null)
            {
                Warn(error);
                RangeBox.Focus();
                return;
            }
        }

        var targets = TargetsBox.Text.Trim();
        if (targets.Length == 0) targets = new AppSettings().InternetTargets;

        s.CloseToTray = CloseToTrayBox.IsChecked == true;
        s.AutoScanMinutes = ScanIntervalBox.SelectedItem is int m ? m : 5;
        s.Interface = string.IsNullOrEmpty(iface) ? null : iface;
        s.ScanRange = range.Length > 0 ? range : null;
        s.AlertNewDevice = AlertNewBox.IsChecked == true;
        s.AlertUnknownOnline = AlertUnknownBox.IsChecked == true;
        s.AlertConflict = AlertConflictBox.IsChecked == true;
        s.AlertCritical = AlertCriticalBox.IsChecked == true;
        s.AlertInternet = AlertInternetBox.IsChecked == true;
        s.HealthIntervalSeconds = HealthIntervalBox.SelectedItem is int h ? h : 60;
        s.InternetTargets = targets;
        s.EventRetentionDays = eventDays;
        s.HealthRetentionDays = healthDays;
        s.BackupsToKeep = backups;

        try
        {
            StartupRegistration.SetEnabled(StartWithWindowsBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
            return;
        }

        _vm.ApplySettings(s);
        DialogResult = true;
    }

    private bool TryDays(TextBox box, int min, int max, out int value)
    {
        if (int.TryParse(box.Text.Trim(), out value) && value >= min && value <= max) return true;
        Warn(Loc.T("Settings.NumberRange", min, max));
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _vm.RunMaintenance(force: true);
            UpdateLastBackup();
            if (path is not null)
                MessageBox.Show(this, Loc.T("Msg.BackupSaved", path), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Maintenance.BackupDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Maintenance.BackupDir}\"") { UseShellExecute = true });
    }
}
