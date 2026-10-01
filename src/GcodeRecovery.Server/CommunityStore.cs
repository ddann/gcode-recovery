using System.Collections.Concurrent;
using GcodeRecovery.Telemetry;
using Microsoft.Data.Sqlite;

namespace GcodeRecovery.Server;

/// <summary>
/// Aggregate-only storage. Persisted: one row per completed job (UTC date without time, version, platform, printer
/// family, method, grams) and the daily peak of concurrent users. Kept in memory only and forgotten after
/// <see cref="ActiveWindow"/>: the random per-launch session ids from heartbeats. No IP address is stored anywhere.
/// </summary>
public sealed class CommunityStore
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(11);

    private readonly string _connectionString;
    private readonly ConcurrentDictionary<Guid, DateTime> _sessions = new();
    private readonly TimeProvider _time;

    public CommunityStore(string databasePath, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS jobs (
                job_id   TEXT PRIMARY KEY,
                day      TEXT NOT NULL,
                version  TEXT NOT NULL,
                platform TEXT NOT NULL,
                printer  TEXT NOT NULL,
                method   TEXT NOT NULL,
                grams    INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS daily_peak (
                day  TEXT PRIMARY KEY,
                peak INTEGER NOT NULL
            );
            """);
    }

    private string Today => _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");

    public int Heartbeat(Heartbeat beat)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _sessions[beat.Session] = now;
        foreach (var (id, seen) in _sessions)
            if (now - seen > ActiveWindow) _sessions.TryRemove(id, out _);
        var active = _sessions.Count;

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO daily_peak(day, peak) VALUES ($d, $p) ON CONFLICT(day) DO UPDATE SET peak = MAX(peak, excluded.peak)";
        cmd.Parameters.AddWithValue("$d", Today);
        cmd.Parameters.AddWithValue("$p", active);
        cmd.ExecuteNonQuery();
        return active;
    }

    public int ActiveNow
    {
        get
        {
            var now = _time.GetUtcNow().UtcDateTime;
            return _sessions.Count(s => now - s.Value <= ActiveWindow);
        }
    }

    /// <summary>Stores a completed job. Returns false when this job id was already counted.</summary>
    public bool AddJob(JobReport job)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO jobs(job_id, day, version, platform, printer, method, grams)
            VALUES ($id, $day, $v, $p, $pr, $m, $g)
            """;
        cmd.Parameters.AddWithValue("$id", job.Job.ToString("N"));
        cmd.Parameters.AddWithValue("$day", Today);
        cmd.Parameters.AddWithValue("$v", job.Version);
        cmd.Parameters.AddWithValue("$p", job.Platform);
        cmd.Parameters.AddWithValue("$pr", job.Printer);
        cmd.Parameters.AddWithValue("$m", job.Method);
        cmd.Parameters.AddWithValue("$g", job.GramsSaved);
        return cmd.ExecuteNonQuery() == 1;
    }

    public CommunityStats Stats()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(grams), 0), COALESCE(MIN(day), $today) FROM jobs";
        cmd.Parameters.AddWithValue("$today", Today);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new CommunityStats(ActiveNow, r.GetInt32(0), r.GetDouble(1), r.GetString(2));
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
