using System.Net;
using System.Windows;
using NetWatcher.Models;
using NetWatcher.Services;
using NetWatcher.ViewModels;

namespace NetWatcher;

public partial class DeviceEditWindow : Window
{
    private static readonly string[] TypeKeys =
    [
        "Type.Computer", "Type.WindowsPc", "Type.PhoneTablet", "Type.Printer", "Type.Camera",
        "Type.Router", "Type.NetworkGear", "Type.TvMedia", "Type.Iot", "Type.Apple",
    ];

    private readonly MainViewModel _vm;
    private readonly DeviceRow _device;

    public DeviceEditWindow(MainViewModel vm, DeviceRow device)
    {
        InitializeComponent();
        _vm = vm;
        _device = device;

        MacBox.Text = device.Mac;
        IpBox.Text = device.Ip ?? "—";
        VendorBox.Text = device.VendorText;
        HostBox.Text = device.Hostname ?? "—";
        PortsBox.Text = device.OpenPorts ?? "—";
        SeenBox.Text = $"{device.FirstSeen:g}   ·   {Loc.T("Col.LastSeen")}: {device.LastSeen?.ToString("g") ?? "—"}";
        HistoryBox.Text = string.Join(Environment.NewLine,
            vm.IpHistory(device).Take(6).Select(h => $"{h.Ip}   ({h.First:g} → {h.Last:g})"));
        PrivateMacNote.Visibility = device.IsRandomMac ? Visibility.Visible : Visibility.Collapsed;

        KnownBox.IsChecked = device.IsKnown;
        CriticalBox.IsChecked = device.IsCritical;
        MutedBox.IsChecked = device.IsMuted;
        NameBox.Text = device.Name ?? device.Hostname ?? "";
        OwnerBox.Text = device.Owner ?? "";
        LocationBox.Text = device.Location ?? "";
        foreach (var k in TypeKeys) TypeBox.Items.Add(Loc.T(k));
        TypeBox.Text = device.ManualType ?? "";
        ReservedBox.Text = device.ReservedIp ?? "";
        NotesBox.Text = device.Notes ?? "";

        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var reserved = ReservedBox.Text.Trim();
        if (reserved.Length > 0)
        {
            if (!IPAddress.TryParse(reserved, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                MessageBox.Show(this, Loc.T("Edit.InvalidIp", reserved), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                ReservedBox.Focus();
                return;
            }
            reserved = addr.ToString();
            if (_vm.ReservedBy(reserved, _device) is { } other &&
                MessageBox.Show(this, Loc.T("Edit.ReservedTaken", reserved, other.DisplayName), Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        var type = TypeBox.Text.Trim();
        // Picking the type the scanner already guessed is the same as leaving it automatic.
        if (type == Loc.T(_device.AutoType ?? "Type.Unknown")) type = "";

        _device.IsKnown = KnownBox.IsChecked == true;
        bool nowCritical = CriticalBox.IsChecked == true;
        if (nowCritical != _device.IsCritical)
        {
            // Start a fresh watch so the first check decides the state without a stale failure count.
            _device.CriticalFailures = 0;
            _device.FirstFailureAt = null;
            if (_device.IsOnline) _device.StatusSince ??= DateTime.Now;
        }
        _device.IsCritical = nowCritical;
        _device.IsMuted = MutedBox.IsChecked == true;
        _device.Name = Blank(NameBox.Text);
        _device.Owner = Blank(OwnerBox.Text);
        _device.Location = Blank(LocationBox.Text);
        _device.ManualType = Blank(type);
        _device.ReservedIp = Blank(reserved);
        _device.Notes = Blank(NotesBox.Text);
        DialogResult = true;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
