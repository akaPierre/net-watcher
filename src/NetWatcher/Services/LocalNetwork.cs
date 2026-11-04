using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetWatcher.Services;

/// <summary>The network interface this PC uses to reach the office LAN, and the range to scan on it.</summary>
public sealed record LocalNetwork(
    string InterfaceName,
    int InterfaceIndex,
    IPAddress Address,
    int PrefixLength,
    IPAddress? Gateway,
    PhysicalAddress Mac,
    int? RangePrefix = null,
    uint? RangeBase = null)
{
    /// <summary>Largest subnet swept automatically; bigger networks are narrowed to the /22 around this PC.</summary>
    private const int MinAutoPrefix = 22;

    /// <summary>Largest custom range accepted (about 4,000 addresses).</summary>
    public const int MinCustomPrefix = 20;

    public int EffectivePrefix => RangePrefix ?? Math.Max(PrefixLength, MinAutoPrefix);

    public IPAddress NetworkAddress => FromUInt(RangeBase ?? (ToUInt(Address) & Mask(EffectivePrefix)));

    public string Cidr => $"{NetworkAddress}/{EffectivePrefix}";

    /// <summary>All usable host addresses in the range (excludes network and broadcast).</summary>
    public IEnumerable<IPAddress> Hosts()
    {
        uint net = ToUInt(NetworkAddress);
        uint broadcast = net | ~Mask(EffectivePrefix);
        if (EffectivePrefix >= 31)
        {
            for (uint a = net; a <= broadcast && a >= net; a++) yield return FromUInt(a);
            yield break;
        }
        for (uint a = net + 1; a < broadcast; a++)
            yield return FromUInt(a);
    }

    public bool Contains(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork &&
        (ToUInt(ip) & Mask(EffectivePrefix)) == ToUInt(NetworkAddress);

    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static IPAddress FromUInt(uint v) =>
        new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    private static uint Mask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    /// <summary>Active IPv4 interfaces, as (name, "name — 192.168.x.y/24") for the settings list.</summary>
    public static List<(string Name, string Description)> ListInterfaces() =>
        Candidates().Select(c => (c.InterfaceName, $"{c.InterfaceName} — {c.Address}/{c.PrefixLength}")).ToList();

    /// <summary>
    /// Picks the interface to monitor: the one named in settings if it is up, otherwise the
    /// active IPv4 interface that has a default gateway. Applies the optional custom range.
    /// </summary>
    public static LocalNetwork? Detect(string? preferredInterface = null, string? customRange = null)
    {
        var all = Candidates();
        var net = all.FirstOrDefault(c => !string.IsNullOrEmpty(preferredInterface) && c.InterfaceName == preferredInterface)
                  ?? all.FirstOrDefault(c => c.Gateway is not null)
                  ?? all.FirstOrDefault();
        if (net is null) return null;

        if (!string.IsNullOrWhiteSpace(customRange) && TryParseRange(net, customRange, out var b, out var p, out _))
            net = net with { RangeBase = b, RangePrefix = p };
        return net;
    }

    /// <summary>Validates a custom scan range like "192.168.1.0/25" against the interface's subnet.</summary>
    public static bool TryParseRange(LocalNetwork net, string text, out uint rangeBase, out int prefix, out string? error)
    {
        rangeBase = 0;
        prefix = 0;
        error = null;
        var parts = text.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32)
        {
            error = Loc.T("Settings.RangeInvalid");
            return false;
        }
        if (prefix < MinCustomPrefix)
        {
            error = Loc.T("Settings.RangeTooBig", MinCustomPrefix);
            return false;
        }
        // The range must be on this PC's own subnet: ARP cannot see past the router.
        uint ifMask = Mask(net.PrefixLength);
        if (prefix < net.PrefixLength || (ToUInt(ip) & ifMask) != (ToUInt(net.Address) & ifMask))
        {
            error = Loc.T("Settings.RangeOutside", $"{FromUInt(ToUInt(net.Address) & ifMask)}/{net.PrefixLength}");
            return false;
        }
        rangeBase = ToUInt(ip) & Mask(prefix);
        return true;
    }

    private static List<LocalNetwork> Candidates()
    {
        var list = new List<LocalNetwork>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var props = nic.GetIPProperties();
            var v4 = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (v4 is null) continue;
            int index;
            try { index = props.GetIPv4Properties().Index; }
            catch { continue; }

            var gw = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            list.Add(new LocalNetwork(nic.Name, index, v4.Address, v4.PrefixLength, gw, nic.GetPhysicalAddress()));
        }
        return list;
    }
}
