using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetWatcher.Services;

/// <summary>
/// Finds the name a device announces about itself, using only standard lookups:
/// NetBIOS (Windows PCs), mDNS/Bonjour (Apple, printers, many IoT) and reverse DNS (router).
/// </summary>
public static class HostnameResolver
{
    private const int TimeoutMs = 1200;

    public static async Task<string?> ResolveAsync(IPAddress ip)
    {
        var netbios = NetBiosAsync(ip);
        var mdns = MdnsAsync(ip);
        var dns = ReverseDnsAsync(ip);
        await Task.WhenAll(netbios, mdns, dns);
        return Clean(netbios.Result) ?? Clean(mdns.Result) ?? Clean(dns.Result);
    }

    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().TrimEnd('.');
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        if (IPAddress.TryParse(name, out _)) return null;
        return name.Length == 0 ? null : name;
    }

    // ---------- Reverse DNS (asks the router's DNS) ----------
    private static async Task<string?> ReverseDnsAsync(IPAddress ip)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs * 2));
            return entry.HostName;
        }
        catch { return null; }
    }

    // ---------- NetBIOS node status (UDP 137) ----------
    private static readonly byte[] NbstatQuery = BuildNbstatQuery();

    private static byte[] BuildNbstatQuery()
    {
        var q = new List<byte> { 0x4E, 0x57, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20 };
        q.AddRange("CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"u8.ToArray()); // "*" encoded
        q.AddRange([0x00, 0x00, 0x21, 0x00, 0x01]); // NBSTAT, IN
        return q.ToArray();
    }

    private static async Task<string?> NetBiosAsync(IPAddress ip)
    {
        var resp = await UdpQueryAsync(new IPEndPoint(ip, 137), NbstatQuery);
        if (resp is null || resp.Length < 57) return null;
        int count = resp[56];
        int off = 57;
        for (int i = 0; i < count && off + 18 <= resp.Length; i++, off += 18)
        {
            var name = Encoding.ASCII.GetString(resp, off, 15).Trim();
            byte suffix = resp[off + 15];
            bool group = (resp[off + 16] & 0x80) != 0;
            if (suffix == 0x00 && !group && name.Length > 0) return name;
        }
        return null;
    }

    // ---------- mDNS reverse lookup (UDP 5353, unicast "one-shot" query) ----------
    private static async Task<string?> MdnsAsync(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        var qname = $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
        var q = new List<byte> { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (var label in qname.Split('.'))
        {
            q.Add((byte)label.Length);
            q.AddRange(Encoding.ASCII.GetBytes(label));
        }
        q.AddRange([0x00, 0x00, 0x0C, 0x80, 0x01]); // PTR, class IN with "unicast response" bit

        var resp = await UdpQueryAsync(new IPEndPoint(ip, 5353), q.ToArray());
        if (resp is null || resp.Length < 12) return null;
        try
        {
            int qd = resp[4] << 8 | resp[5];
            int an = resp[6] << 8 | resp[7];
            int off = 12;
            for (int i = 0; i < qd; i++) { ReadName(resp, ref off); off += 4; }
            for (int i = 0; i < an && off + 10 <= resp.Length; i++)
            {
                ReadName(resp, ref off);
                int type = resp[off] << 8 | resp[off + 1];
                int rdlen = resp[off + 8] << 8 | resp[off + 9];
                off += 10;
                if (type == 12)
                {
                    int p = off;
                    return ReadName(resp, ref p);
                }
                off += rdlen;
            }
        }
        catch { /* malformed reply */ }
        return null;
    }

    private static string ReadName(byte[] b, ref int off)
    {
        var labels = new List<string>();
        int pos = off, jumps = 0;
        bool jumped = false;
        while (pos < b.Length)
        {
            int len = b[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= b.Length || ++jumps > 20) break;
                if (!jumped) off = pos + 2;
                jumped = true;
                pos = (len & 0x3F) << 8 | b[pos + 1];
                continue;
            }
            pos++;
            if (pos + len > b.Length) break;
            labels.Add(Encoding.UTF8.GetString(b, pos, len));
            pos += len;
        }
        if (!jumped) off = pos;
        return string.Join('.', labels);
    }

    private static async Task<byte[]?> UdpQueryAsync(IPEndPoint target, byte[] payload)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(payload, target);
            using var cts = new CancellationTokenSource(TimeoutMs);
            var result = await udp.ReceiveAsync(cts.Token);
            return result.Buffer;
        }
        catch { return null; } // timeout or "port unreachable"
    }
}
