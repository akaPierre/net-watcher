using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NetWatcher.Models;
using NetWatcher.Services;

namespace NetWatcher.ViewModels;

/// <summary>
/// Internet health and critical devices. Every minute (configurable) it pings the router and the
/// internet, and checks each critical device with a fresh ARP request + ping.
/// Two failures in a row are needed before anything is declared down, so a single lost packet
/// never raises an alarm.
/// </summary>
public partial class MainViewModel
{
    private const int FailuresToDeclareDown = 2;
    private static readonly TimeSpan HealthBuffer = TimeSpan.FromDays(7);

    private readonly DispatcherTimer _healthTimer = new();
    private readonly List<HealthSample> _samples = [];
    private bool _healthRunning;
    private int _gatewayFails, _internetFails;
    private DateTime? _gatewayFirstFail, _internetFirstFail;
    private OutageRow? _routerOutage, _internetOutage;

    public ObservableCollection<OutageRow> Outages { get; } = [];
    public ListCollectionView CriticalView { get; private set; } = null!;

    public int[] HealthRangeOptions { get; } = [1, 6, 24, 168];

    [ObservableProperty] private bool _internetUp = true;
    [ObservableProperty] private bool _routerUp = true;
    [ObservableProperty] private bool _healthKnown;
    [ObservableProperty] private string _internetStatusText = "—";
    [ObservableProperty] private string _routerStatusText = "—";
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _outagesWeekText = "—";
    [ObservableProperty] private string _criticalSummaryText = "—";
    [ObservableProperty] private int _criticalDownCount;
    [ObservableProperty] private bool _criticalAllUp = true;
    [ObservableProperty] private int _healthRangeHours = 24;
    [ObservableProperty] private IReadOnlyList<HealthSample> _chartSamples = [];
    [ObservableProperty] private DateTime _chartStart = DateTime.Now.AddHours(-24);
    [ObservableProperty] private DateTime _chartEnd = DateTime.Now;

    private void InitHealth()
    {
        CriticalView = new ListCollectionView(Devices) { Filter = o => ((DeviceRow)o).IsCritical, IsLiveFiltering = true };
        CriticalView.LiveFilteringProperties.Add(nameof(DeviceRow.IsCritical));
        CriticalView.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(DeviceRow.IsOnline), System.ComponentModel.ListSortDirection.Ascending));
        CriticalView.IsLiveSorting = true;
        CriticalView.LiveSortingProperties.Add(nameof(DeviceRow.IsOnline));
        OnPropertyChanged(nameof(CriticalView));

        _db.CloseDanglingOutages();
        foreach (var o in _db.LoadOutages(200)) Outages.Add(o);
        _samples.AddRange(_db.LoadHealthSamples(DateTime.Now - HealthBuffer));
        RefreshHealthSummary();

        _healthTimer.Tick += async (_, _) => await HealthTickAsync();
        ApplyHealthTimer();
        _ = HealthTickAsync();
    }

    private void ApplyHealthTimer()
    {
        _healthTimer.Stop();
        _healthTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, Settings.HealthIntervalSeconds));
        _healthTimer.Start();
    }

    partial void OnHealthRangeHoursChanged(int value) => RefreshChart();

    private async Task HealthTickAsync()
    {
        if (_healthRunning) return;
        var net = _net ?? DetectNetwork();
        if (net is null) return;
        _healthRunning = true;
        try
        {
            var now = DateTime.Now;

            // --- probes, all in parallel ---
            var gatewayTask = net.Gateway is { } gw ? PingTwiceAsync(gw) : Task.FromResult<long?>(null);
            var internetTask = InternetPingAsync(Settings.InternetTargetList.ToList());
            var critical = Devices.Where(d => d.IsCritical && d.Ip is not null).ToList();
            var criticalTasks = critical.Select(d => NetworkScanner.ProbeAsync(net, IPAddress.Parse(d.Ip!))).ToList();
            await Task.WhenAll(gatewayTask, internetTask, Task.WhenAll(criticalTasks));

            long? gatewayMs = gatewayTask.Result;
            long? internetMs = internetTask.Result;
            var alerts = new List<(string, string)>();

            // --- record sample ---
            var sample = new HealthSample(now, gatewayMs, internetMs);
            _samples.Add(sample);
            _samples.RemoveAll(s => s.Time < now - HealthBuffer);
            _db.AddHealthSample(now, gatewayMs, internetMs);

            // --- router (office network) ---
            if (net.Gateway is not null)
            {
                if (gatewayMs is null)
                {
                    _gatewayFirstFail ??= now;
                    if (++_gatewayFails == FailuresToDeclareDown && _routerOutage is null)
                    {
                        _routerOutage = OpenOutage(OutageRow.KindRouter, _gatewayFirstFail.Value);
                        AddEvent(EventKind.RouterDown, null, net.Gateway.ToString(), Loc.T("Msg.RouterDown", net.Gateway));
                        if (Settings.AlertInternet) alerts.Add((Loc.T("Alert.RouterDown"), Loc.T("Msg.RouterDown", net.Gateway)));
                    }
                }
                else
                {
                    if (_routerOutage is not null)
                    {
                        var o = CloseOutage(_routerOutage, now);
                        var msg = Loc.T("Msg.RouterUp", OutageRow.FormatDuration(o.Duration));
                        AddEvent(EventKind.RouterUp, null, net.Gateway.ToString(), msg);
                        if (Settings.AlertInternet) alerts.Add((Loc.T("Alert.RouterUp"), msg));
                        _routerOutage = null;
                    }
                    _gatewayFails = 0;
                    _gatewayFirstFail = null;
                }
            }

            // --- internet (only blamed on the operator when the router itself answers) ---
            if (internetMs is null)
            {
                _internetFirstFail ??= now;
                if (++_internetFails == FailuresToDeclareDown && _internetOutage is null && _routerOutage is null)
                {
                    _internetOutage = OpenOutage(OutageRow.KindInternet, _internetFirstFail.Value);
                    AddEvent(EventKind.InternetDown, null, null, Loc.T("Msg.InternetDown"));
                    if (Settings.AlertInternet) alerts.Add((Loc.T("Alert.InternetDown"), Loc.T("Msg.InternetDown")));
                }
            }
            else
            {
                if (_internetOutage is not null)
                {
                    var o = CloseOutage(_internetOutage, now);
                    var msg = Loc.T("Msg.InternetUp", OutageRow.FormatDuration(o.Duration));
                    AddEvent(EventKind.InternetUp, null, null, msg);
                    if (Settings.AlertInternet) alerts.Add((Loc.T("Alert.InternetUp"), msg));
                    _internetOutage = null;
                }
                _internetFails = 0;
                _internetFirstFail = null;
            }

            // --- critical devices ---
            for (int i = 0; i < critical.Count; i++)
            {
                var d = critical[i];
                var (mac, ping) = criticalTasks[i].Result;
                // It must be *this* device answering: if someone else now holds its IP, it is still missing.
                bool up = mac is not null && mac.Equals(d.Mac, StringComparison.OrdinalIgnoreCase);
                if (up)
                {
                    bool wasDown = !d.IsOnline;
                    if (wasDown && d.StatusSince is { } since && d.CriticalFailures >= FailuresToDeclareDown)
                    {
                        var msg = Loc.T("Msg.CriticalUp", d.DisplayName, d.Ip, OutageRow.FormatDuration(now - since));
                        AddEvent(EventKind.CriticalUp, d.Mac, d.Ip, msg);
                        if (Settings.AlertCritical && !d.IsMuted) alerts.Add((Loc.T("Alert.CriticalUp"), msg));
                    }
                    if (wasDown || d.StatusSince is null) d.StatusSince = now;
                    d.IsOnline = true;
                    d.LastSeen = now;
                    d.PingMs = ping;
                    d.CriticalFailures = 0;
                    d.FirstFailureAt = null;
                }
                else
                {
                    d.FirstFailureAt ??= now;
                    d.CriticalFailures++;
                    // Declared down exactly once per failure streak (also when it was already down at startup).
                    if (d.CriticalFailures == FailuresToDeclareDown)
                    {
                        d.IsOnline = false;
                        d.PingMs = null;
                        d.StatusSince = d.FirstFailureAt;
                        var msg = Loc.T("Msg.CriticalDown", d.DisplayName, d.Ip);
                        AddEvent(EventKind.CriticalDown, d.Mac, d.Ip, msg);
                        if (Settings.AlertCritical && !d.IsMuted) alerts.Add((Loc.T("Alert.CriticalDown"), msg));
                    }
                }
            }

            foreach (var d in critical) d.RefreshStatusSince();
            RefreshHealthSummary();
            UpdateCounts();
            RebuildIpMap();
            RaiseAlerts(alerts);
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Status.HealthFailed", ex.Message);
        }
        finally
        {
            _healthRunning = false;
        }
    }

    private OutageRow OpenOutage(string kind, DateTime start)
    {
        var id = _db.StartOutage(kind, start);
        var o = new OutageRow(id, kind, start, null);
        Outages.Insert(0, o);
        return o;
    }

    private OutageRow CloseOutage(OutageRow open, DateTime end)
    {
        _db.EndOutage(open.Id, end);
        var closed = open with { End = end };
        int i = Outages.IndexOf(open);
        if (i >= 0) Outages[i] = closed;
        return closed;
    }

    private static async Task<long?> PingTwiceAsync(IPAddress ip)
    {
        // One retry so a single dropped packet is not counted as a failure.
        return await NetworkScanner.PingAsync(ip, 1000) ?? await NetworkScanner.PingAsync(ip, 1000);
    }

    /// <summary>Best round-trip to any of the internet test addresses; null if none answered.</summary>
    private static async Task<long?> InternetPingAsync(List<string> targets)
    {
        if (targets.Count == 0) return null;
        async Task<long?> One(string host)
        {
            try
            {
                using var p = new System.Net.NetworkInformation.Ping();
                var r = await p.SendPingAsync(host, 1500);
                return r.Status == System.Net.NetworkInformation.IPStatus.Success ? r.RoundtripTime : null;
            }
            catch { return null; }
        }
        var first = await Task.WhenAll(targets.Select(One));
        var best = first.Where(v => v is not null).Min();
        if (best is not null) return best;
        var retry = await Task.WhenAll(targets.Select(One));
        return retry.Where(v => v is not null).Min();
    }

    private void RefreshHealthSummary()
    {
        var now = DateTime.Now;
        var last = _samples.LastOrDefault();
        HealthKnown = last is not null;
        InternetUp = _internetOutage is null;
        RouterUp = _routerOutage is null;

        InternetStatusText = last is null ? "—"
            : _internetOutage is not null ? Loc.T("Health.DownSince", _internetOutage.Start.ToString("t"))
            : last.InternetMs is { } im ? Loc.T("Health.UpMs", im)
            : Loc.T("Health.Checking");
        RouterStatusText = last is null ? "—"
            : _routerOutage is not null ? Loc.T("Health.DownSince", _routerOutage.Start.ToString("t"))
            : last.GatewayMs is { } gm ? Loc.T("Health.UpMs", gm)
            : Loc.T("Health.Checking");

        var day = _samples.Where(s => s.Time >= now.AddHours(-24)).ToList();
        UptimeText = day.Count == 0 ? "—"
            : (100.0 * day.Count(s => s.InternetMs is not null) / day.Count).ToString("0.0", CultureInfo.CurrentCulture) + "%";

        var week = Outages.Where(o => o.Start >= now.AddDays(-7)).ToList();
        OutagesWeekText = week.Count == 0 ? "0"
            : $"{week.Count} · {OutageRow.FormatDuration(TimeSpan.FromTicks(week.Sum(o => o.Duration.Ticks)))}";

        var critical = Devices.Where(d => d.IsCritical).ToList();
        CriticalDownCount = critical.Count(d => !d.IsOnline && d.CriticalFailures >= FailuresToDeclareDown);
        CriticalAllUp = CriticalDownCount == 0;
        CriticalSummaryText = critical.Count == 0 ? Loc.T("Health.NoCritical")
            : Loc.T("Health.CriticalSummary", critical.Count - CriticalDownCount, critical.Count);

        RefreshChart();
    }

    private void RefreshChart()
    {
        var end = DateTime.Now;
        var start = end.AddHours(-HealthRangeHours);
        ChartStart = start;
        ChartEnd = end;
        ChartSamples = _samples.Where(s => s.Time >= start).ToList();
    }

    public void ExportOutagesCsv(string path)
    {
        var sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(sep, new[] { Loc.T("Col.What"), Loc.T("Col.Start"), Loc.T("Col.End"), Loc.T("Col.Duration") }.Select(Csv)));
        foreach (var o in Outages.OrderBy(o => o.Start))
            sb.AppendLine(string.Join(sep, new[] { o.KindText, o.Start.ToString("G"), o.End?.ToString("G") ?? "", o.DurationText }.Select(Csv)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
