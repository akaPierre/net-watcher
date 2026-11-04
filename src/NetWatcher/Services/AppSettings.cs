using System.Globalization;

namespace NetWatcher.Services;

/// <summary>User preferences, stored in the database's settings table.</summary>
public sealed class AppSettings
{
    // General
    public int AutoScanMinutes { get; set; } = 5;
    public bool CloseToTray { get; set; } = true;

    // Network
    public string? Interface { get; set; }          // null = automatic
    public string? ScanRange { get; set; }          // null = whole subnet, e.g. "192.168.1.0/25"

    // Notifications
    public bool AlertNewDevice { get; set; } = true;
    public bool AlertUnknownOnline { get; set; } = true;
    public bool AlertConflict { get; set; } = true;
    public bool AlertCritical { get; set; } = true;
    public bool AlertInternet { get; set; } = true;

    // Internet & critical device monitoring
    public int HealthIntervalSeconds { get; set; } = 60;
    public string InternetTargets { get; set; } = "1.1.1.1, 8.8.8.8";

    // Data
    public int EventRetentionDays { get; set; } = 180;
    public int HealthRetentionDays { get; set; } = 90;
    public int BackupsToKeep { get; set; } = 8;

    public static readonly int[] HealthIntervalOptions = [30, 60, 120, 300];

    public IEnumerable<string> InternetTargetList =>
        InternetTargets.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static AppSettings Load(Database db)
    {
        var s = new AppSettings();
        s.AutoScanMinutes = Int(db, "autoScanMinutes", s.AutoScanMinutes, 0, 1440);
        s.CloseToTray = Bool(db, "closeToTray", s.CloseToTray);
        s.Interface = Str(db, "interface");
        s.ScanRange = Str(db, "scanRange");
        s.AlertNewDevice = Bool(db, "alertNewDevice", s.AlertNewDevice);
        s.AlertUnknownOnline = Bool(db, "alertUnknownOnline", s.AlertUnknownOnline);
        s.AlertConflict = Bool(db, "alertConflict", s.AlertConflict);
        s.AlertCritical = Bool(db, "alertCritical", s.AlertCritical);
        s.AlertInternet = Bool(db, "alertInternet", s.AlertInternet);
        s.HealthIntervalSeconds = Int(db, "healthIntervalSeconds", s.HealthIntervalSeconds, 10, 3600);
        s.InternetTargets = Str(db, "internetTargets") ?? s.InternetTargets;
        s.EventRetentionDays = Int(db, "eventRetentionDays", s.EventRetentionDays, 7, 3650);
        s.HealthRetentionDays = Int(db, "healthRetentionDays", s.HealthRetentionDays, 7, 3650);
        s.BackupsToKeep = Int(db, "backupsToKeep", s.BackupsToKeep, 1, 100);
        return s;
    }

    public void Save(Database db)
    {
        db.SetSetting("autoScanMinutes", AutoScanMinutes.ToString(CultureInfo.InvariantCulture));
        db.SetSetting("closeToTray", CloseToTray ? "1" : "0");
        db.SetSetting("interface", Interface);
        db.SetSetting("scanRange", ScanRange);
        db.SetSetting("alertNewDevice", AlertNewDevice ? "1" : "0");
        db.SetSetting("alertUnknownOnline", AlertUnknownOnline ? "1" : "0");
        db.SetSetting("alertConflict", AlertConflict ? "1" : "0");
        db.SetSetting("alertCritical", AlertCritical ? "1" : "0");
        db.SetSetting("alertInternet", AlertInternet ? "1" : "0");
        db.SetSetting("healthIntervalSeconds", HealthIntervalSeconds.ToString(CultureInfo.InvariantCulture));
        db.SetSetting("internetTargets", InternetTargets);
        db.SetSetting("eventRetentionDays", EventRetentionDays.ToString(CultureInfo.InvariantCulture));
        db.SetSetting("healthRetentionDays", HealthRetentionDays.ToString(CultureInfo.InvariantCulture));
        db.SetSetting("backupsToKeep", BackupsToKeep.ToString(CultureInfo.InvariantCulture));
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    private static string? Str(Database db, string key) =>
        db.GetSetting(key) is { Length: > 0 } v ? v : null;

    private static bool Bool(Database db, string key, bool def) =>
        db.GetSetting(key) is { } v ? v == "1" : def;

    private static int Int(Database db, string key, int def, int min, int max) =>
        int.TryParse(db.GetSetting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max) : def;
}
