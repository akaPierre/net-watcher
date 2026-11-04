using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetWatcher.Models;
using NetWatcher.Services;

namespace NetWatcher.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan EnrichEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan ConflictHold = TimeSpan.FromMinutes(30);

    private readonly Database _db = new(AppPaths.DatabaseFile);
    private readonly OuiDatabase _oui = new();
    private readonly Dictionary<string, DeviceRow> _byMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _conflictAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _conflictLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _multiIpReported = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new();
    private Dictionary<string, string> _lastIpOwner = new();
    private LocalNetwork? _net;
    private int _scanCount;
    private bool _firstRun;
    private bool _enriching;

    /// <summary>Raised for things the IT person should notice (title, message).</summary>
    public event Action<string, string>? Alert;

    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ListCollectionView DevicesView { get; }
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<IpTile> IpMap { get; } = [];

    public int[] IntervalOptions { get; } = [0, 1, 2, 5, 10, 15, 30];

    [ObservableProperty] private string _networkText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _scanProgress;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _knownCount;
    [ObservableProperty] private int _unknownCount;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private int _autoScanMinutes = 5;

    public MainViewModel()
    {
        DevicesView = new ListCollectionView(Devices)
        {
            Filter = FilterDevice,
            IsLiveFiltering = true,
            IsLiveSorting = true,
        };
        foreach (var p in new[] { nameof(DeviceRow.IsOnline), nameof(DeviceRow.IsKnown), nameof(DeviceRow.HasConflict), nameof(DeviceRow.IsCritical) })
            DevicesView.LiveFilteringProperties.Add(p);
        DevicesView.LiveSortingProperties.Add(nameof(DeviceRow.IsOnline));
        DevicesView.LiveSortingProperties.Add(nameof(DeviceRow.IpSortKey));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceRow.IsOnline), ListSortDirection.Descending));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceRow.IpSortKey), ListSortDirection.Ascending));

        _timer.Tick += async (_, _) => await ScanNowAsync();
    }

    // =====================================================================
    // Startup
    // =====================================================================

    public AppSettings Settings { get; private set; } = new();

    public Database Db => _db;

    public async Task InitializeAsync()
    {
        Settings = AppSettings.Load(_db);
        AutoScanMinutes = Settings.AutoScanMinutes;
        ApplyTimer();

        foreach (var d in _db.LoadDevices())
        {
            _byMac[d.Mac] = d;
            Devices.Add(d);
        }
        _firstRun = Devices.Count == 0;
        foreach (var e in _db.LoadEvents(1000)) Events.Add(e);

        _net = DetectNetwork();
        UpdateNetworkText();
        RebuildIpMap();
        UpdateCounts();

        StartupRegistration.RefreshPathIfEnabled();
        InitHealth();
        StartMaintenance();

        StatusText = Loc.T("Status.LoadingVendors");
        await _oui.LoadAsync();
        foreach (var d in Devices.Where(d => d.Vendor is null))
            d.Vendor = _oui.Lookup(d.Mac);

        await ScanNowAsync();
    }

    private LocalNetwork? DetectNetwork() => LocalNetwork.Detect(Settings.Interface, Settings.ScanRange);

    /// <summary>Called by the Settings window after the user clicks Save.</summary>
    public void ApplySettings(AppSettings updated)
    {
        bool networkChanged = updated.Interface != Settings.Interface || updated.ScanRange != Settings.ScanRange;
        Settings = updated;
        Settings.Save(_db);
        AutoScanMinutes = Settings.AutoScanMinutes;
        ApplyTimer();
        ApplyHealthTimer();
        if (networkChanged)
        {
            _net = DetectNetwork();
            UpdateNetworkText();
            RebuildIpMap();
            _ = ScanNowAsync();
        }
    }

    private void UpdateNetworkText()
    {
        NetworkText = _net is null
            ? Loc.T("Status.NoNetwork")
            : Loc.T("Header.Network", _net.InterfaceName, _net.Address, _net.PrefixLength, _net.Gateway?.ToString() ?? "—", _net.Cidr);
    }

    // =====================================================================
    // Scanning
    // =====================================================================

    [RelayCommand]
    private async Task ScanNowAsync()
    {
        if (IsScanning) return;
        _net = DetectNetwork();
        UpdateNetworkText();
        if (_net is null)
        {
            StatusText = Loc.T("Status.NoNetwork");
            return;
        }

        IsScanning = true;
        ScanProgress = 0;
        StatusText = Loc.T("Status.Scanning", _net.Cidr);
        try
        {
            var progress = new Progress<double>(p => ScanProgress = p * 100);
            var hits = await NetworkScanner.ScanAsync(_net, progress, CancellationToken.None);
            ApplyScan(hits);
            _scanCount++;
            _ = EnrichAsync();
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Status.ScanFailed", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ApplyScan(List<ScanHit> hits)
    {
        var net = _net!;
        var now = DateTime.Now;
        bool quiet = _scanCount == 0; // first scan of the session: summarize instead of one event per device
        var alerts = new List<(string Title, string Msg)>();
        var newIpOwner = new Dictionary<string, string>();
        var seenMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? gatewayIp = net.Gateway?.ToString();
        string selfIp = net.Address.ToString();
        int newCount = 0;

        foreach (var group in hits.GroupBy(h => h.Mac, StringComparer.OrdinalIgnoreCase))
        {
            var mac = group.Key;
            var answers = group.ToList();
            _byMac.TryGetValue(mac, out var row);

            // If a device answers on several IPs, keep showing the one it already had.
            var primary = answers.FirstOrDefault(h => h.Ip.ToString() == row?.Ip) ?? answers[0];
            var ip = primary.Ip.ToString();
            seenMacs.Add(mac);
            foreach (var h in answers)
            {
                newIpOwner[h.Ip.ToString()] = mac;
                _db.RecordIp(mac, h.Ip.ToString(), now);
            }
            if (answers.Count > 1 && _multiIpReported.Add(mac))
            {
                var ips = string.Join(", ", answers.Select(a => a.Ip));
                AddEvent(EventKind.Info, mac, ip, Loc.T("Msg.MultipleIps", row?.DisplayName ?? mac, ips));
            }

            bool isSelf = ip == selfIp;
            bool isGateway = ip == gatewayIp;

            if (row is null)
            {
                row = new DeviceRow(mac) { FirstSeen = now, Vendor = _oui.Lookup(mac) };
                if (isSelf) row.Hostname = Environment.MachineName;
                row.IsKnown = isSelf || isGateway; // this PC and the router are approved from the start
                row.AutoType = DeviceClassifier.Guess(row.Vendor, [], row.IsRandomMac, isGateway, isSelf);
                _byMac[mac] = row;
                Devices.Add(row);
                newCount++;

                if (!_firstRun)
                {
                    AddEvent(EventKind.NewDevice, mac, ip, Loc.T("Msg.NewDevice", row.DisplayName, ip, mac));
                    QueueAlert(alerts, Settings.AlertNewDevice, row, Loc.T("Alert.NewDevice"), Loc.T("Alert.DeviceLine", row.DisplayName, ip, mac));
                }
            }
            else
            {
                if (row.Ip is not null && row.Ip != ip)
                    AddEvent(EventKind.IpChanged, mac, ip, Loc.T("Msg.IpChanged", row.DisplayName, row.Ip, ip));

                // Critical devices' up/down transitions are owned by the health monitor (checked every minute).
                if (!row.IsOnline && !quiet && !row.IsCritical)
                {
                    if (row.IsKnown)
                        AddEvent(EventKind.Online, mac, ip, Loc.T("Msg.Online", row.DisplayName, ip));
                    else
                    {
                        AddEvent(EventKind.UnknownOnline, mac, ip, Loc.T("Msg.UnknownOnline", row.DisplayName, ip, mac));
                        QueueAlert(alerts, Settings.AlertUnknownOnline, row, Loc.T("Alert.UnknownOnline"), Loc.T("Alert.DeviceLine", row.DisplayName, ip, mac));
                    }
                }
            }

            row.Ip = ip;
            row.PingMs = primary.PingMs;
            row.LastSeen = now;
            row.MissedScans = 0;
            if (!row.IsCritical)
            {
                if (!row.IsOnline) row.StatusSince = now;
                row.IsOnline = true;
            }

            // --- Conflict 1: someone is using an IP you reserved for a different device ---
            foreach (var owner in Devices.Where(d => d.ReservedIp == ip && !d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase)))
                FlagConflict(ip, row, owner, Loc.T("Msg.ReservedTaken", ip, owner.DisplayName, row.DisplayName, mac), alerts);

            // --- Conflict 2: the IP keeps switching between two devices ---
            // ARP returns only one answer per scan, so two devices fighting over one IP show up as
            // the owner flipping back and forth (A → B → A). A single change is just DHCP reusing the address.
            if (_lastIpOwner.TryGetValue(ip, out var prevMac) &&
                !prevMac.Equals(mac, StringComparison.OrdinalIgnoreCase) &&
                _byMac.TryGetValue(prevMac, out var prev) &&
                RecentOwners(ip).Contains(mac, StringComparer.OrdinalIgnoreCase))
            {
                FlagConflict(ip, row, prev, Loc.T("Msg.IpFlapping", ip, prev.DisplayName, row.DisplayName), alerts);
            }
        }

        // Devices that did not answer.
        foreach (var d in Devices.Where(d => d.IsOnline && !d.IsCritical && !seenMacs.Contains(d.Mac)))
        {
            d.MissedScans++;
            if (d.MissedScans >= 2)
            {
                d.IsOnline = false;
                d.StatusSince = now;
                d.PingMs = null;
                AddEvent(EventKind.Offline, d.Mac, d.Ip, Loc.T("Msg.Offline", d.DisplayName, d.Ip ?? "—"));
            }
        }

        // Expire old conflict flags.
        foreach (var d in Devices)
            d.HasConflict = _conflictAt.TryGetValue(d.Mac, out var at) && now - at < ConflictHold;

        RememberIpOwners(newIpOwner);
        _lastIpOwner = newIpOwner;

        foreach (var d in Devices.Where(d => seenMacs.Contains(d.Mac))) _db.SaveDevice(d);

        if (quiet)
        {
            int unknownOnline = Devices.Count(d => d.IsOnline && !d.IsKnown);
            if (_firstRun)
                AddEvent(EventKind.Info, null, null, Loc.T("Msg.InitialInventory", newCount));
            else if (newCount > 0)
                AddEvent(EventKind.Info, null, null, Loc.T("Msg.NewSinceLastRun", newCount));
            if (unknownOnline > 0 && Settings.AlertUnknownOnline)
                alerts.Insert(0, (Loc.T("Alert.UnknownSummaryTitle"), Loc.T("Alert.UnknownSummary", unknownOnline)));
            if (_firstRun) alerts.Clear(); // everything is "unknown" until you register it; no point alarming
        }
        _firstRun = false;

        UpdateCounts();
        RebuildIpMap();
        StatusText = Loc.T("Status.LastScan", now.ToString("T", CultureInfo.CurrentCulture), OnlineCount, UnknownCount);
        RaiseAlerts(alerts);
    }

    // Keeps a short memory of which MACs have used each IP in recent scans, to tell DHCP reuse from flapping.
    private readonly Dictionary<string, Queue<string>> _ipOwnerHistory = new();
    private IEnumerable<string> RecentOwners(string ip) =>
        _ipOwnerHistory.TryGetValue(ip, out var q) ? q : Enumerable.Empty<string>();

    private void RememberIpOwners(Dictionary<string, string> owners)
    {
        foreach (var (ip, mac) in owners)
        {
            if (!_ipOwnerHistory.TryGetValue(ip, out var q)) _ipOwnerHistory[ip] = q = new Queue<string>();
            q.Enqueue(mac);
            while (q.Count > 6) q.Dequeue();
        }
    }

    private void FlagConflict(string ip, DeviceRow a, DeviceRow b, string message, List<(string, string)> alerts)
    {
        var now = DateTime.Now;
        _conflictAt[a.Mac] = now;
        _conflictAt[b.Mac] = now;
        var key = ip + "|" + string.Join("|", new[] { a.Mac, b.Mac }.Order());
        if (_conflictLogged.TryGetValue(key, out var last) && now - last < ConflictHold) return;
        _conflictLogged[key] = now;
        AddEvent(EventKind.Conflict, a.Mac, ip, message);
        // A conflict involves two devices; only stay silent if both are muted.
        if (Settings.AlertConflict && !(a.IsMuted && b.IsMuted)) alerts.Add((Loc.T("Alert.Conflict"), message));
    }

    /// <summary>Adds a notification unless that alert type is turned off or the device is muted.</summary>
    private static void QueueAlert(List<(string, string)> alerts, bool enabled, DeviceRow? device, string title, string message)
    {
        if (enabled && device?.IsMuted != true) alerts.Add((title, message));
    }

    private void RaiseAlerts(List<(string Title, string Msg)> alerts)
    {
        if (alerts.Count == 0) return;
        if (alerts.Count <= 2)
        {
            foreach (var (t, m) in alerts) Alert?.Invoke(t, m);
            return;
        }
        Alert?.Invoke(Loc.T("Alert.ManyTitle", alerts.Count), string.Join("\n", alerts.Take(4).Select(a => a.Msg)));
    }

    /// <summary>Looks up hostnames and service hints for online devices, in the background.</summary>
    private async Task EnrichAsync()
    {
        if (_enriching || _net is null) return;
        _enriching = true;
        try
        {
            var net = _net;
            var now = DateTime.Now;
            var targets = Devices
                .Where(d => d.IsOnline && d.Ip is not null && (d.EnrichedAt is null || now - d.EnrichedAt > EnrichEvery))
                .ToList();
            using var gate = new SemaphoreSlim(16);
            await Task.WhenAll(targets.Select(async d =>
            {
                await gate.WaitAsync();
                try
                {
                    var ip = IPAddress.Parse(d.Ip!);
                    bool isSelf = ip.Equals(net.Address);
                    bool isGateway = net.Gateway is not null && ip.Equals(net.Gateway);
                    var nameTask = isSelf ? Task.FromResult<string?>(Environment.MachineName) : HostnameResolver.ResolveAsync(ip);
                    var portsTask = DeviceClassifier.ProbePortsAsync(ip);
                    await Task.WhenAll(nameTask, portsTask);

                    if (nameTask.Result is { } name) d.Hostname = name;
                    d.OpenPorts = portsTask.Result.Length > 0 ? string.Join(", ", portsTask.Result) : null;
                    d.AutoType = DeviceClassifier.Guess(d.Vendor, portsTask.Result, d.IsRandomMac, isGateway, isSelf);
                    d.EnrichedAt = DateTime.Now;
                    _db.SaveDevice(d);
                }
                catch { /* device went away mid-lookup */ }
                finally { gate.Release(); }
            }));
            RebuildIpMap();
        }
        finally
        {
            _enriching = false;
        }
    }

    // =====================================================================
    // Device registry actions
    // =====================================================================

    public void SaveDevice(DeviceRow d)
    {
        _db.SaveDevice(d);
        UpdateCounts();
        RebuildIpMap();
        if (d.IsCritical) _ = HealthTickAsync(); // check a newly watched device right away
    }

    public IReadOnlyList<(string Ip, DateTime First, DateTime Last)> IpHistory(DeviceRow d) => _db.IpHistory(d.Mac);

    /// <summary>Returns the device that already has this IP reserved, if any (to warn before saving).</summary>
    public DeviceRow? ReservedBy(string ip, DeviceRow except) =>
        Devices.FirstOrDefault(d => d != except && d.ReservedIp == ip);

    [RelayCommand]
    private void MarkKnown(IList? items) => SetKnown(items, true);

    [RelayCommand]
    private void MarkUnknown(IList? items) => SetKnown(items, false);

    private void SetKnown(IList? items, bool known)
    {
        if (items is null) return;
        foreach (var d in items.OfType<DeviceRow>().ToList())
        {
            d.IsKnown = known;
            _db.SaveDevice(d);
        }
        UpdateCounts();
        RebuildIpMap();
    }

    public void Forget(IEnumerable<DeviceRow> rows)
    {
        foreach (var d in rows.ToList())
        {
            _db.DeleteDevice(d.Mac);
            _byMac.Remove(d.Mac);
            Devices.Remove(d);
        }
        UpdateCounts();
        RebuildIpMap();
    }

    [RelayCommand]
    private void ClearEvents()
    {
        _db.ClearEvents();
        Events.Clear();
    }

    [RelayCommand]
    private async Task UpdateVendorsAsync()
    {
        StatusText = Loc.T("Status.LoadingVendors");
        await _oui.LoadAsync(forceDownload: true);
        foreach (var d in Devices)
        {
            d.Vendor = _oui.Lookup(d.Mac) ?? d.Vendor;
            _db.SaveDevice(d);
        }
        StatusText = Loc.T("Status.VendorsUpdated", _oui.Count);
    }

    public void ExportCsv(string path)
    {
        var sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator; // ";" on pt-BR Excel, "," on en-US
        var sb = new StringBuilder();
        string[] header =
        [
            Loc.T("Col.Status"), Loc.T("Col.Ip"), Loc.T("Col.Name"), Loc.T("Col.Type"), Loc.T("Col.Vendor"),
            Loc.T("Col.Hostname"), Loc.T("Col.Mac"), Loc.T("Col.Owner"), Loc.T("Col.Location"),
            Loc.T("Col.Known"), Loc.T("Col.ReservedIp"), Loc.T("Col.Ports"), Loc.T("Col.FirstSeen"),
            Loc.T("Col.LastSeen"), Loc.T("Col.Notes"),
        ];
        sb.AppendLine(string.Join(sep, header.Select(Csv)));
        foreach (var d in Devices.OrderByDescending(d => d.IsOnline).ThenBy(d => d.IpSortKey))
        {
            string[] cols =
            [
                d.IsOnline ? Loc.T("Status.Online") : Loc.T("Status.Offline"),
                d.Ip ?? "", d.Name ?? "", d.TypeText, d.VendorText, d.Hostname ?? "", d.Mac,
                d.Owner ?? "", d.Location ?? "", d.IsKnown ? Loc.T("Common.Yes") : Loc.T("Common.No"),
                d.ReservedIp ?? "", d.OpenPorts ?? "", d.FirstSeen.ToString("g"), d.LastSeen?.ToString("g") ?? "",
                d.Notes ?? "",
            ];
            sb.AppendLine(string.Join(sep, cols.Select(Csv)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    // =====================================================================
    // View helpers
    // =====================================================================

    private void AddEvent(EventKind kind, string? mac, string? ip, string message)
    {
        var e = _db.AddEvent(kind, mac, ip, message);
        Events.Insert(0, e);
        while (Events.Count > 1000) Events.RemoveAt(Events.Count - 1);
    }

    private void UpdateCounts()
    {
        TotalCount = Devices.Count;
        OnlineCount = Devices.Count(d => d.IsOnline);
        KnownCount = Devices.Count(d => d.IsKnown);
        UnknownCount = Devices.Count(d => d.IsOnline && !d.IsKnown);
        ConflictCount = Devices.Count(d => d.HasConflict);
    }

    private bool FilterDevice(object o)
    {
        var d = (DeviceRow)o;
        bool pass = FilterIndex switch
        {
            1 => d.IsOnline,
            2 => !d.IsKnown,
            3 => !d.IsOnline,
            4 => d.HasConflict,
            5 => d.IsCritical,
            _ => true,
        };
        if (!pass) return false;
        var q = SearchText?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return new[] { d.Ip, d.Mac, d.Name, d.Hostname, d.Vendor, d.Owner, d.Location, d.Notes, d.TypeText }
            .Any(s => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    partial void OnSearchTextChanged(string value) => DevicesView.Refresh();
    partial void OnFilterIndexChanged(int value) => DevicesView.Refresh();

    partial void OnAutoScanMinutesChanged(int value)
    {
        Settings.AutoScanMinutes = value;
        _db.SetSetting("autoScanMinutes", value.ToString(CultureInfo.InvariantCulture));
        ApplyTimer();
    }

    private void ApplyTimer()
    {
        _timer.Stop();
        if (AutoScanMinutes > 0)
        {
            _timer.Interval = TimeSpan.FromMinutes(AutoScanMinutes);
            _timer.Start();
        }
    }

    private void RebuildIpMap()
    {
        IpMap.Clear();
        if (_net is null) return;
        bool wide = _net.EffectivePrefix < 24;
        var online = Devices.Where(d => d.IsOnline && d.Ip is not null)
            .GroupBy(d => d.Ip!).ToDictionary(g => g.Key, g => g.First());
        var reserved = Devices.Where(d => d.ReservedIp is not null)
            .GroupBy(d => d.ReservedIp!).ToDictionary(g => g.Key, g => g.First());
        string self = _net.Address.ToString();
        string? gw = _net.Gateway?.ToString();

        foreach (var addr in _net.Hosts())
        {
            var ip = addr.ToString();
            var b = addr.GetAddressBytes();
            var label = wide ? $"{b[2]}.{b[3]}" : b[3].ToString();
            online.TryGetValue(ip, out var dev);
            reserved.TryGetValue(ip, out var res);

            IpState state;
            string tip;
            if (dev is not null)
            {
                state = dev.HasConflict ? IpState.Conflict
                    : ip == self ? IpState.Self
                    : ip == gw ? IpState.Gateway
                    : dev.IsKnown ? IpState.Known : IpState.Unknown;
                tip = $"{ip}\n{dev.DisplayName}\n{dev.TypeText} · {dev.VendorText}\n{dev.Mac}";
                if (res is not null && res != dev) tip += "\n" + Loc.T("Map.ReservedFor", res.DisplayName);
            }
            else if (res is not null)
            {
                state = IpState.Reserved;
                tip = $"{ip}\n" + Loc.T("Map.ReservedFor", res.DisplayName);
            }
            else
            {
                state = IpState.Free;
                tip = $"{ip}\n" + Loc.T("Map.Free");
            }
            IpMap.Add(new IpTile(label, ip, state, tip, dev ?? res));
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _healthTimer.Stop();
        _maintenanceTimer.Stop();
        _db.Dispose();
    }
}
