namespace NetWatcher.Models;

public enum IpState
{
    Free,       // nobody answered on this address
    Reserved,   // free right now, but you reserved it for a device
    Known,      // an approved device is using it
    Unknown,    // an unregistered device is using it
    Conflict,   // possible IP conflict detected
    Self,       // this PC
    Gateway,    // the router
}

public sealed record IpTile(string Label, string Ip, IpState State, string Tooltip, DeviceRow? Device);
