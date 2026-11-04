using System.Net;
using System.Net.Sockets;

namespace NetWatcher.Services;

/// <summary>
/// Guesses what kind of device something is from its manufacturer and a handful of
/// well-known service ports (a plain TCP connect, nothing is sent or logged in to).
/// </summary>
public static class DeviceClassifier
{
    public static readonly int[] HintPorts = [22, 80, 443, 445, 554, 631, 3389, 8009, 9100, 62078];

    public static async Task<int[]> ProbePortsAsync(IPAddress ip)
    {
        var checks = HintPorts.Select(async p => await IsOpenAsync(ip, p, 600) ? p : 0);
        return (await Task.WhenAll(checks)).Where(p => p != 0).ToArray();
    }

    private static async Task<bool> IsOpenAsync(IPAddress ip, int port, int timeoutMs)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await s.ConnectAsync(ip, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Returns a localization key for the guessed device type.</summary>
    public static string Guess(string? vendor, IReadOnlyCollection<int> ports, bool randomMac, bool isGateway, bool isSelf)
    {
        if (isSelf) return "Type.ThisComputer";
        if (isGateway) return "Type.Router";
        if (ports.Contains(9100) || ports.Contains(631)) return "Type.Printer";
        if (ports.Contains(554)) return "Type.Camera";
        if (ports.Contains(62078)) return "Type.AppleMobile";
        if (ports.Contains(8009)) return "Type.TvMedia";
        if (ports.Contains(3389) || ports.Contains(445)) return "Type.WindowsPc";

        var v = vendor?.ToLowerInvariant() ?? "";
        if (Has(v, "hp ", "hewlett", "epson", "brother", "canon", "lexmark", "kyocera", "ricoh", "xerox")) return "Type.Printer";
        if (Has(v, "hikvision", "dahua", "intelbras", "axis comm", "uniview")) return "Type.Camera";
        if (Has(v, "apple")) return "Type.Apple";
        if (Has(v, "samsung", "xiaomi", "motorola", "huawei", "oppo", "vivo mobile", "oneplus", "realme", "lg electronics")) return "Type.PhoneTablet";
        if (Has(v, "tp-link", "ubiquiti", "mikrotik", "cisco", "aruba", "netgear", "d-link", "encore", "zyxel")) return "Type.NetworkGear";
        if (Has(v, "espressif", "tuya", "shelly", "sonoff", "amazon", "google")) return "Type.Iot";
        if (Has(v, "dell", "lenovo", "asus", "acer", "intel", "realtek", "micro-star", "gigabyte", "microsoft")) return "Type.Computer";
        if (randomMac) return "Type.PrivateMac";
        return "Type.Unknown";
    }

    private static bool Has(string s, params string[] needles) => needles.Any(s.Contains);
}
