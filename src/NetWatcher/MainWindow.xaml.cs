using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NetWatcher.Models;
using NetWatcher.Services;
using NetWatcher.ViewModels;

namespace NetWatcher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>When false, closing the window just hides it to the tray.</summary>
    public bool AllowClose { get; set; }

    public event EventHandler? HiddenToTray;

    /// <summary>Raised when the window is closed and "keep running in the tray" is off.</summary>
    public event EventHandler? ExitRequested;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            if (_vm.Settings.CloseToTray)
            {
                Hide();
                HiddenToTray?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Defer so the close in progress finishes before the app shuts down.
                Dispatcher.BeginInvoke(() => ExitRequested?.Invoke(this, EventArgs.Empty));
            }
        }
        base.OnClosing(e);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_vm) { Owner = this }.ShowDialog();

    private void CriticalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(CriticalGrid, src) is DataGridRow { Item: DeviceRow d })
            OpenEditor(d);
    }

    private void ExportOutages_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"internet-outages-{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _vm.ExportOutagesCsv(dlg.FileName);
            _vm.StatusText = Loc.T("Status.Exported", dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IEnumerable<DeviceRow> SelectedDevices => DeviceGrid.SelectedItems.OfType<DeviceRow>();

    private void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only react to double-clicks on a row, not on the header or scrollbar.
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(DeviceGrid, src) is DataGridRow { Item: DeviceRow d })
            OpenEditor(d);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceGrid.SelectedItem is DeviceRow d) OpenEditor(d);
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IpTile { Device: { } d } }) OpenEditor(d);
    }

    private void OpenEditor(DeviceRow d)
    {
        var dlg = new DeviceEditWindow(_vm, d) { Owner = this };
        if (dlg.ShowDialog() == true) _vm.SaveDevice(d);
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e) =>
        CopyText(string.Join(Environment.NewLine, SelectedDevices.Select(d => d.Ip).Where(s => s is not null)));

    private void CopyMac_Click(object sender, RoutedEventArgs e) =>
        CopyText(string.Join(Environment.NewLine, SelectedDevices.Select(d => d.Mac)));

    private static void CopyText(string text)
    {
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedDevices.ToList();
        if (rows.Count == 0) return;
        if (MessageBox.Show(this, Loc.T("Confirm.Forget", rows.Count), Loc.T("App.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _vm.Forget(rows);
    }

    private void ClearEvents_Click(object sender, RoutedEventArgs e) => _vm.ClearEventsCommand.Execute(null);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"network-devices-{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _vm.ExportCsv(dlg.FileName);
            _vm.StatusText = Loc.T("Status.Exported", dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
