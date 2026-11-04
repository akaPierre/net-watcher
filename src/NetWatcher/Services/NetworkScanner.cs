using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;

namespace NetWatcher.Services;

public sealed record ScanHit(IPAddress Ip, string Mac, long? PingMs);

/// <summary>Sweeps every address in the subnet with a ping + ARP request.</summary>
public static class NetworkScanner
{
    public static async Task<List<ScanHit>> ScanAsync(LocalNetwork net, IProgress<double>? progress, CancellationToken ct)
    {
        var hosts = net.Hosts().ToList();
        var hits = new ConcurrentBag<ScanHit>();
        using var gate = new SemaphoreSlim(128);
        int done = 0;

        var tasks = hosts.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (ip.Equals(net.Address))
                {
                    hits.Add(new ScanHit(ip, Arp.Format(net.Mac), 0));
                    return;
                }

                var (mac, rtt) = await ProbeAsync(net, ip, ct);
                if (mac is not null) hits.Add(new ScanHit(ip, mac, rtt));
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done) / (double)hosts.Count);
            }
        });

        await Task.WhenAll(tasks);
        return hits.OrderBy(h => LocalNetwork.ToUInt(h.Ip)).ToList();
    }

    /// <summary>
    /// Checks one address: a fresh ARP request plus a ping, in parallel.
    /// Returns the MAC that answered (null if nobody did) and the ping time if it replied.
    /// </summary>
    public static async Task<(string? Mac, long? PingMs)> ProbeAsync(LocalNetwork net, IPAddress ip, CancellationToken ct = default)
    {
        var pingTask = PingAsync(ip, 800);
        // Many phones and firewalled PCs ignore ping but must answer ARP to stay on the network.
        var arpTask = Task.Run(() => Arp.Resolve(ip, net.InterfaceIndex), ct);
        await Task.WhenAll(pingTask, arpTask);
        var mac = arpTask.Result;
        return (mac is null ? null : Arp.Format(mac), pingTask.Result);
    }

    public static async Task<long?> PingAsync(IPAddress ip, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, timeoutMs);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch { return null; }
    }
}
