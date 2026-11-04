using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using NetWatcher.Models;

namespace NetWatcher.Services;

/// <summary>Local SQLite storage: the device registry, event log and settings.</summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS devices (
                mac          TEXT PRIMARY KEY,
                name         TEXT,
                owner        TEXT,
                location     TEXT,
                notes        TEXT,
                manual_type  TEXT,
                reserved_ip  TEXT,
                is_known     INTEGER NOT NULL DEFAULT 0,
                last_ip      TEXT,
                vendor       TEXT,
                hostname     TEXT,
                auto_type    TEXT,
                open_ports   TEXT,
                first_seen   TEXT NOT NULL,
                last_seen    TEXT
            );
            CREATE TABLE IF NOT EXISTS ip_history (
                mac        TEXT NOT NULL,
                ip         TEXT NOT NULL,
                first_seen TEXT NOT NULL,
                last_seen  TEXT NOT NULL,
                PRIMARY KEY (mac, ip)
            );
            CREATE TABLE IF NOT EXISTS events (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                time    TEXT NOT NULL,
                kind    TEXT NOT NULL,
                mac     TEXT,
                ip      TEXT,
                message TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_time ON events(time);
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            CREATE TABLE IF NOT EXISTS health_samples (
                time        TEXT PRIMARY KEY,
                gateway_ms  INTEGER,
                internet_ms INTEGER
            );
            CREATE TABLE IF NOT EXISTS outages (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                kind       TEXT NOT NULL,
                start_time TEXT NOT NULL,
                end_time   TEXT
            );
            """);
        AddColumnIfMissing("devices", "is_critical", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("devices", "is_muted", "INTEGER NOT NULL DEFAULT 0");
    }

    private void AddColumnIfMissing(string table, string column, string definition)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if ((long)cmd.ExecuteScalar()! == 0) Exec($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    // ---------------- Devices ----------------

    public List<DeviceRow> LoadDevices()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT mac,name,owner,location,notes,manual_type,reserved_ip,is_known,last_ip,vendor,hostname,auto_type,open_ports,first_seen,last_seen,is_critical,is_muted FROM devices";
        using var r = cmd.ExecuteReader();
        var list = new List<DeviceRow>();
        while (r.Read())
        {
            list.Add(new DeviceRow(r.GetString(0))
            {
                Name = Str(r, 1),
                Owner = Str(r, 2),
                Location = Str(r, 3),
                Notes = Str(r, 4),
                ManualType = Str(r, 5),
                ReservedIp = Str(r, 6),
                IsKnown = r.GetInt64(7) != 0,
                Ip = Str(r, 8),
                Vendor = Str(r, 9),
                Hostname = Str(r, 10),
                AutoType = Str(r, 11),
                OpenPorts = Str(r, 12),
                FirstSeen = ParseTime(r.GetString(13)).ToLocalTime(),
                LastSeen = r.IsDBNull(14) ? null : ParseTime(r.GetString(14)).ToLocalTime(),
                IsCritical = r.GetInt64(15) != 0,
                IsMuted = r.GetInt64(16) != 0,
            });
        }
        return list;
    }

    public void SaveDevice(DeviceRow d)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices (mac,name,owner,location,notes,manual_type,reserved_ip,is_known,last_ip,vendor,hostname,auto_type,open_ports,first_seen,last_seen,is_critical,is_muted)
            VALUES ($mac,$name,$owner,$location,$notes,$mtype,$rip,$known,$ip,$vendor,$host,$atype,$ports,$first,$last,$crit,$muted)
            ON CONFLICT(mac) DO UPDATE SET
                name=$name, owner=$owner, location=$location, notes=$notes, manual_type=$mtype,
                reserved_ip=$rip, is_known=$known, last_ip=$ip, vendor=$vendor, hostname=$host,
                auto_type=$atype, open_ports=$ports, last_seen=$last, is_critical=$crit, is_muted=$muted
            """;
        cmd.Parameters.AddWithValue("$crit", d.IsCritical ? 1 : 0);
        cmd.Parameters.AddWithValue("$muted", d.IsMuted ? 1 : 0);
        cmd.Parameters.AddWithValue("$mac", d.Mac);
        cmd.Parameters.AddWithValue("$name", Db(d.Name));
        cmd.Parameters.AddWithValue("$owner", Db(d.Owner));
        cmd.Parameters.AddWithValue("$location", Db(d.Location));
        cmd.Parameters.AddWithValue("$notes", Db(d.Notes));
        cmd.Parameters.AddWithValue("$mtype", Db(d.ManualType));
        cmd.Parameters.AddWithValue("$rip", Db(d.ReservedIp));
        cmd.Parameters.AddWithValue("$known", d.IsKnown ? 1 : 0);
        cmd.Parameters.AddWithValue("$ip", Db(d.Ip));
        cmd.Parameters.AddWithValue("$vendor", Db(d.Vendor));
        cmd.Parameters.AddWithValue("$host", Db(d.Hostname));
        cmd.Parameters.AddWithValue("$atype", Db(d.AutoType));
        cmd.Parameters.AddWithValue("$ports", Db(d.OpenPorts));
        cmd.Parameters.AddWithValue("$first", Time(d.FirstSeen));
        cmd.Parameters.AddWithValue("$last", d.LastSeen is { } ls ? Time(ls) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteDevice(string mac)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM devices WHERE mac=$mac; DELETE FROM ip_history WHERE mac=$mac;";
        cmd.Parameters.AddWithValue("$mac", mac);
        cmd.ExecuteNonQuery();
    }

    public void RecordIp(string mac, string ip, DateTime now)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ip_history (mac, ip, first_seen, last_seen) VALUES ($mac,$ip,$now,$now)
            ON CONFLICT(mac, ip) DO UPDATE SET last_seen=$now
            """;
        cmd.Parameters.AddWithValue("$mac", mac);
        cmd.Parameters.AddWithValue("$ip", ip);
        cmd.Parameters.AddWithValue("$now", Time(now));
        cmd.ExecuteNonQuery();
    }

    public List<(string Ip, DateTime First, DateTime Last)> IpHistory(string mac)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT ip, first_seen, last_seen FROM ip_history WHERE mac=$mac ORDER BY last_seen DESC";
        cmd.Parameters.AddWithValue("$mac", mac);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, DateTime, DateTime)>();
        while (r.Read()) list.Add((r.GetString(0), ParseTime(r.GetString(1)).ToLocalTime(), ParseTime(r.GetString(2)).ToLocalTime()));
        return list;
    }

    // ---------------- Events ----------------

    public EventRow AddEvent(EventKind kind, string? mac, string? ip, string message)
    {
        var now = DateTime.UtcNow;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO events (time,kind,mac,ip,message) VALUES ($t,$k,$m,$i,$msg); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", Time(now));
        cmd.Parameters.AddWithValue("$k", kind.ToString());
        cmd.Parameters.AddWithValue("$m", Db(mac));
        cmd.Parameters.AddWithValue("$i", Db(ip));
        cmd.Parameters.AddWithValue("$msg", message);
        var id = (long)cmd.ExecuteScalar()!;
        return new EventRow(id, now.ToLocalTime(), kind, mac, ip, message);
    }

    public List<EventRow> LoadEvents(int limit)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,time,kind,mac,ip,message FROM events ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<EventRow>();
        while (r.Read())
        {
            Enum.TryParse<EventKind>(r.GetString(2), out var kind);
            list.Add(new EventRow(r.GetInt64(0), ParseTime(r.GetString(1)).ToLocalTime(), kind, Str(r, 3), Str(r, 4), r.GetString(5)));
        }
        return list;
    }

    public void ClearEvents() => Exec("DELETE FROM events");

    // ---------------- Internet health ----------------

    public void AddHealthSample(DateTime time, long? gatewayMs, long? internetMs)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO health_samples (time, gateway_ms, internet_ms) VALUES ($t,$g,$i)";
        cmd.Parameters.AddWithValue("$t", Time(time));
        cmd.Parameters.AddWithValue("$g", gatewayMs is { } g ? g : DBNull.Value);
        cmd.Parameters.AddWithValue("$i", internetMs is { } i ? i : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<HealthSample> LoadHealthSamples(DateTime since)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT time, gateway_ms, internet_ms FROM health_samples WHERE time >= $since ORDER BY time";
        cmd.Parameters.AddWithValue("$since", Time(since));
        using var r = cmd.ExecuteReader();
        var list = new List<HealthSample>();
        while (r.Read())
            list.Add(new HealthSample(ParseTime(r.GetString(0)).ToLocalTime(),
                r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
        return list;
    }

    public long StartOutage(string kind, DateTime start)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO outages (kind, start_time) VALUES ($k,$s); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$s", Time(start));
        return (long)cmd.ExecuteScalar()!;
    }

    public void EndOutage(long id, DateTime end)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE outages SET end_time=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$e", Time(end));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Closes outages left open by a crash or shutdown, ending them at the last recorded sample.</summary>
    public void CloseDanglingOutages()
    {
        Exec("""
            UPDATE outages SET end_time = COALESCE(
                (SELECT MAX(time) FROM health_samples WHERE time >= outages.start_time), start_time)
            WHERE end_time IS NULL
            """);
    }

    public List<OutageRow> LoadOutages(int limit)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, start_time, end_time FROM outages ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<OutageRow>();
        while (r.Read())
            list.Add(new OutageRow(r.GetInt64(0), r.GetString(1), ParseTime(r.GetString(2)).ToLocalTime(),
                r.IsDBNull(3) ? null : ParseTime(r.GetString(3)).ToLocalTime()));
        return list;
    }

    // ---------------- Housekeeping ----------------

    /// <summary>Deletes old events and health samples. Returns how many rows were removed.</summary>
    public int Prune(int eventDays, int healthDays)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM events WHERE time < $ev;
            DELETE FROM health_samples WHERE time < $hs;
            DELETE FROM outages WHERE end_time IS NOT NULL AND end_time < $hs;
            """;
        cmd.Parameters.AddWithValue("$ev", Time(DateTime.UtcNow.AddDays(-eventDays)));
        cmd.Parameters.AddWithValue("$hs", Time(DateTime.UtcNow.AddDays(-healthDays)));
        int n = cmd.ExecuteNonQuery();
        Exec("PRAGMA optimize;");
        return n;
    }

    /// <summary>Writes a consistent copy of the live database (safe while the app is running).</summary>
    public void BackupTo(string path)
    {
        using var dest = new SqliteConnection($"Data Source={path};Pooling=False");
        dest.Open();
        _conn.BackupDatabase(dest);
    }

    // ---------------- Settings ----------------

    public string? GetSetting(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string? value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key,value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", Db(value));
        cmd.ExecuteNonQuery();
    }

    // ---------------- helpers ----------------

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static object Db(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();
    private static string Time(DateTime t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTime ParseTime(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public void Dispose() => _conn.Dispose();
}
