using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NetWatcher.Services;

/// <summary>ARP lookups through the built-in Windows IP Helper API (no extra drivers needed).</summary>
public static class Arp
{
    // MIB_IPNET_ROW2 (netioapi.h), IPv4 only.
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    private unsafe struct MibIpNetRow2
    {
        [FieldOffset(0)] public ushort Family;     // SOCKADDR_INET.si_family
        [FieldOffset(4)] public uint Address;      // sin_addr
        [FieldOffset(28)] public uint InterfaceIndex;
        [FieldOffset(40)] public fixed byte PhysicalAddress[32];
        [FieldOffset(72)] public uint PhysicalAddressLength;
        [FieldOffset(76)] public int State;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int ResolveIpNetEntry2(ref MibIpNetRow2 row, IntPtr sourceAddress);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physAddrLen);

    private static bool _useLegacy;

    /// <summary>
    /// Asks "who has this IP?" on the LAN and returns the MAC, or null if nobody answered.
    /// Always sends a fresh ARP request: Windows' ARP cache keeps "stale" entries for devices that
    /// already left, so trusting the cache would show departed devices as still online.
    /// </summary>
    public static PhysicalAddress? Resolve(IPAddress ip, int interfaceIndex)
    {
        if (!_useLegacy)
        {
            try
            {
                return ResolveFresh(ip, interfaceIndex, out bool unsupported) is { } mac ? mac
                    : unsupported ? ResolveLegacy(ip) : null;
            }
            catch (EntryPointNotFoundException)
            {
                _useLegacy = true;
            }
        }
        return ResolveLegacy(ip);
    }

    private static unsafe PhysicalAddress? ResolveFresh(IPAddress ip, int interfaceIndex, out bool unsupported)
    {
        unsupported = false;
        var row = new MibIpNetRow2
        {
            Family = 2, // AF_INET
            Address = BitConverter.ToUInt32(ip.GetAddressBytes(), 0),
            InterfaceIndex = (uint)interfaceIndex,
        };
        int rc = ResolveIpNetEntry2(ref row, IntPtr.Zero);
        if (rc == 5 /* ERROR_ACCESS_DENIED */ || rc == 50 /* ERROR_NOT_SUPPORTED */)
        {
            _useLegacy = true;
            unsupported = true;
            return null;
        }
        if (rc != 0 || row.PhysicalAddressLength != 6) return null;

        var mac = new byte[6];
        for (int i = 0; i < 6; i++) mac[i] = row.PhysicalAddress[i];
        return Valid(mac) ? new PhysicalAddress(mac) : null;
    }

    private static PhysicalAddress? ResolveLegacy(IPAddress ip)
    {
        var mac = new byte[6];
        int len = mac.Length;
        uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
        int rc = SendARP(dest, 0, mac, ref len);
        if (rc != 0 || len != 6) return null;
        return Valid(mac) ? new PhysicalAddress(mac) : null;
    }

    private static bool Valid(byte[] mac) => !mac.All(b => b == 0) && !mac.All(b => b == 0xFF);

    public static string Format(PhysicalAddress mac) =>
        string.Join(':', mac.GetAddressBytes().Select(b => b.ToString("X2")));

    /// <summary>
    /// A "locally administered" MAC (second hex digit 2, 6, A or E) is usually a randomized
    /// private address from a phone/laptop, so it has no manufacturer.
    /// </summary>
    public static bool IsRandomized(string mac) =>
        mac.Length >= 2 && (Convert.ToByte(mac[..2], 16) & 0x02) != 0;
}
