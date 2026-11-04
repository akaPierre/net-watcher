using System.IO;
using System.Net.Http;

namespace NetWatcher.Services;

/// <summary>
/// Maps the first bytes of a MAC address to its manufacturer, using the public
/// IEEE registry (via Wireshark's "manuf" file, which also includes the smaller MA-M/MA-S blocks).
/// The list is downloaded once and cached locally; it is refreshed every couple of months.
/// </summary>
public sealed class OuiDatabase
{
    private static readonly string[] Sources =
    [
        "https://www.wireshark.org/download/automated/data/manuf",
        "https://standards-oui.ieee.org/oui/oui.csv",
    ];

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);

    private readonly string _cachePath = Path.Combine(AppPaths.DataDir, "oui.txt");

    // Key: uppercase hex prefix without separators (6, 7 or 9 hex digits).
    private Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public int Count => _map.Count;

    public async Task LoadAsync(bool forceDownload = false)
    {
        bool stale = !File.Exists(_cachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath) > MaxAge;
        if (forceDownload || stale)
        {
            try { await DownloadAsync(); }
            catch { /* offline: fall back to whatever cache we have */ }
        }

        if (File.Exists(_cachePath))
            _map = await Task.Run(() => Parse(File.ReadAllLines(_cachePath)));
    }

    public string? Lookup(string mac)
    {
        if (_map.Count == 0) return null;
        var hex = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
        foreach (var len in new[] { 9, 7, 6 })
            if (hex.Length >= len && _map.TryGetValue(hex[..len], out var name))
                return name;
        return null;
    }

    private async Task DownloadAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetWatcher/0.1");
        foreach (var url in Sources)
        {
            try
            {
                var text = await http.GetStringAsync(url);
                if (Parse(text.Split('\n')).Count < 1000) continue; // not the file we expected
                Directory.CreateDirectory(AppPaths.DataDir);
                await File.WriteAllTextAsync(_cachePath, text);
                return;
            }
            catch { /* try next source */ }
        }
    }

    private static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.Contains('\t'))
            {
                // Wireshark: "00:00:0C<TAB>Cisco<TAB>Cisco Systems, Inc" or "00:1B:C5:00:00:00/36<TAB>..."
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                var prefix = parts[0];
                int bits = 24;
                int slash = prefix.IndexOf('/');
                if (slash > 0)
                {
                    bits = int.Parse(prefix[(slash + 1)..]);
                    prefix = prefix[..slash];
                }
                var hex = prefix.Replace(":", "").Replace("-", "").ToUpperInvariant();
                int digits = bits / 4;
                if (hex.Length < digits) continue;
                var name = parts.Length >= 3 && parts[2].Trim().Length > 0 ? parts[2].Trim() : parts[1].Trim();
                map[hex[..digits]] = name;
            }
            else if (line.StartsWith("MA-", StringComparison.Ordinal))
            {
                // IEEE CSV: Registry,Assignment,"Organization Name",Address
                var cols = SplitCsv(line);
                if (cols.Count >= 3 && cols[1].Length is 6 or 7 or 9)
                    map[cols[1].ToUpperInvariant()] = cols[2].Trim();
            }
        }
        return map;
    }

    private static List<string> SplitCsv(string line)
    {
        var cols = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { cols.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cols.Add(cur.ToString());
        return cols;
    }
}
