namespace NetWatcher.Models;

public enum EventKind
{
    Info,
    NewDevice,
    UnknownOnline,
    Online,
    Offline,
    IpChanged,
    Conflict,
    CriticalDown,
    CriticalUp,
    InternetDown,
    InternetUp,
    RouterDown,
    RouterUp,
    Maintenance,
}

public sealed record EventRow(long Id, DateTime Time, EventKind Kind, string? Mac, string? Ip, string Message)
{
    public string KindText => Services.Loc.T("Event." + Kind);
    public bool IsAlert => Kind is EventKind.NewDevice or EventKind.UnknownOnline or EventKind.Conflict
        or EventKind.CriticalDown or EventKind.InternetDown or EventKind.RouterDown;
}
