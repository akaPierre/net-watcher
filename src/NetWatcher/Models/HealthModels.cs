namespace NetWatcher.Models;

/// <summary>One health check: router and internet round-trip times (null = no reply).</summary>
public sealed record HealthSample(DateTime Time, long? GatewayMs, long? InternetMs);

/// <summary>A period when the router (office network) or the internet was unreachable.</summary>
public sealed record OutageRow(long Id, string Kind, DateTime Start, DateTime? End)
{
    public const string KindInternet = "Internet";
    public const string KindRouter = "Router";

    public string KindText => Services.Loc.T("Outage." + Kind);

    public TimeSpan Duration => (End ?? DateTime.Now) - Start;

    public string DurationText => FormatDuration(Duration) + (End is null ? " …" : "");

    public static string FormatDuration(TimeSpan d) =>
        d.TotalMinutes < 1 ? $"{(int)d.TotalSeconds}s"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m {d.Seconds:00}s"
        : d.TotalDays < 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m"
        : $"{(int)d.TotalDays}d {d.Hours:00}h";
}
