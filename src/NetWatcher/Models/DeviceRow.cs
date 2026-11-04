using CommunityToolkit.Mvvm.ComponentModel;
using NetWatcher.Services;

namespace NetWatcher.Models;

/// <summary>One physical device, identified by its MAC address.</summary>
public partial class DeviceRow : ObservableObject
{
    public DeviceRow(string mac) => Mac = mac;

    public string Mac { get; }

    // --- What you register about it ---
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayName))] private string? _name;
    [ObservableProperty] private string? _owner;
    [ObservableProperty] private string? _location;
    [ObservableProperty] private string? _notes;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TypeText))] private string? _manualType;
    [ObservableProperty] private string? _reservedIp;
    [ObservableProperty] private bool _isKnown;
    [ObservableProperty] private bool _isCritical;   // watched every minute; alert when it goes down
    [ObservableProperty] private bool _isMuted;      // still tracked and logged, but never pops a notification
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusSinceText))] private DateTime? _statusSince;

    // --- What the scanner found ---
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IpSortKey))] private string? _ip;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayName))] private string? _vendor;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayName))] private string? _hostname;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TypeText))] private string? _autoType;
    [ObservableProperty] private string? _openPorts;
    [ObservableProperty] private DateTime _firstSeen;
    [ObservableProperty] private DateTime? _lastSeen;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _hasConflict;
    [ObservableProperty] private long? _pingMs;

    /// <summary>Not persisted: when hostname/ports were last looked up in this session.</summary>
    public DateTime? EnrichedAt { get; set; }

    /// <summary>Consecutive scans this device did not answer (phones sleep, so we allow one miss).</summary>
    public int MissedScans { get; set; }

    /// <summary>Consecutive failed health checks (critical devices only).</summary>
    public int CriticalFailures { get; set; }

    /// <summary>Time of the first failed check in the current failure streak.</summary>
    public DateTime? FirstFailureAt { get; set; }

    /// <summary>"Up for 3h 12m" / "Down for 4m 10s" for the critical devices panel.</summary>
    public string StatusSinceText => StatusSince is { } t
        ? Loc.T(IsOnline ? "Health.UpFor" : "Health.DownFor", OutageFormat(DateTime.Now - t))
        : "—";

    public void RefreshStatusSince() => OnPropertyChanged(nameof(StatusSinceText));

    private static string OutageFormat(TimeSpan d) => OutageRow.FormatDuration(d);

    public bool IsRandomMac => Arp.IsRandomized(Mac);

    public uint IpSortKey =>
        Ip is not null && System.Net.IPAddress.TryParse(Ip, out var a) ? LocalNetwork.ToUInt(a) : uint.MaxValue;

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name! :
        !string.IsNullOrWhiteSpace(Hostname) ? Hostname! :
        Vendor ?? (IsRandomMac ? Loc.T("Vendor.Private") : Loc.T("Vendor.Unknown"));

    public string TypeText =>
        !string.IsNullOrWhiteSpace(ManualType) ? ManualType! : Loc.T(AutoType ?? "Type.Unknown");

    public string VendorText => Vendor ?? (IsRandomMac ? Loc.T("Vendor.Private") : Loc.T("Vendor.Unknown"));

    partial void OnVendorChanged(string? value) => OnPropertyChanged(nameof(VendorText));
    partial void OnIsOnlineChanged(bool value) => OnPropertyChanged(nameof(StatusSinceText));
}
