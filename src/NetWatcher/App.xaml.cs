using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using NetWatcher.Services;
using NetWatcher.ViewModels;
using Forms = System.Windows.Forms;

namespace NetWatcher;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private bool _toldAboutTray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\NetWatcher.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Net Watcher is already running (check the tray icon near the clock).", "Net Watcher");
            Shutdown();
            return;
        }

        // Show dates/numbers the way this PC is configured (e.g. dd/MM/yyyy on pt-BR Windows).
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        // The scanner runs many short ARP lookups in parallel.
        ThreadPool.GetMinThreads(out var w, out var io);
        ThreadPool.SetMinThreads(Math.Max(w, 160), io);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Net Watcher", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _vm = new MainViewModel();
        _vm.Alert += ShowAlert;

        _window = new MainWindow(_vm);
        _window.ExitRequested += (_, _) => ExitApp();
        _window.HiddenToTray += (_, _) =>
        {
            if (_toldAboutTray) return;
            _toldAboutTray = true;
            ShowAlert(Loc.T("App.Title"), Loc.T("Tray.StillRunning"));
        };

        CreateTray();

        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (!minimized) _window.Show();

        await _vm.InitializeAsync();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Tray.Open"), null, (_, _) => ShowWindow()).Font =
            new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(Loc.T("Tray.Scan"), null, (_, _) => _vm?.ScanNowCommand.Execute(null));
        menu.Items.Add(Loc.T("Tray.DataFolder"), null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.Exit"), null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Shield,
            Text = "Net Watcher",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ShowWindow();
        };
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
    }

    private void ShowAlert(string title, string message)
    {
        if (_tray is null) return;
        // Windows shows these as toast notifications.
        _tray.ShowBalloonTip(8000, title, message.Length > 250 ? message[..250] + "…" : message, Forms.ToolTipIcon.Warning);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        if (_window is not null) _window.AllowClose = true;
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _vm?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
