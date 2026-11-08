using Dapper;
using Microsoft.Data.Sqlite;
using PrepPulse.Core;

namespace PrepPulse.Data;

/// <summary>
/// SQLite persistence (WAL mode, one short-lived pooled connection per call, so the sampler thread and dashboard
/// requests never share a connection). Times are stored as UTC epoch milliseconds and converted to local on read.
/// </summary>
public sealed class SqliteStore : ISessionStore, IReportSource
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly TimeZoneInfo _zone;

    public SqliteStore(string dbPath, TimeZoneInfo? zone = null)
    {
        var full = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();
        _zone = zone ?? TimeZoneInfo.Local;
        Migrate();
    }

    public string DatabasePath => new SqliteConnectionStringBuilder(_connectionString).DataSource;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode = WAL;");
        var version = c.ExecuteScalar<long>("PRAGMA user_version;");
        if (version >= SchemaVersion) return;

        using var tx = c.BeginTransaction();
        c.Execute(SchemaV1, transaction: tx);
        c.Execute($"PRAGMA user_version = {SchemaVersion};", transaction: tx);
        tx.Commit();
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS sessions (
            id             INTEGER PRIMARY KEY,
            plan_slug      TEXT    NOT NULL,
            plan_name      TEXT    NOT NULL,
            started_at     INTEGER NOT NULL,
            ended_at       INTEGER,
            last_heartbeat INTEGER,
            end_reason     TEXT
        );
        CREATE TABLE IF NOT EXISTS focus_segments (
            id         INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id),
            started_at INTEGER NOT NULL,
            ended_at   INTEGER NOT NULL,
            process    TEXT    NOT NULL,
            domain     TEXT,
            title      TEXT,
            list       TEXT    NOT NULL,
            plan_slug  TEXT,
            topic      TEXT,
            category   TEXT,
            entry      TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_segments_time ON focus_segments(started_at);
        CREATE INDEX IF NOT EXISTS ix_segments_plan ON focus_segments(plan_slug, list);
        CREATE TABLE IF NOT EXISTS idle_gaps (
            id         INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id),
            started_at INTEGER NOT NULL,
            ended_at   INTEGER NOT NULL,
            label      TEXT,
            plan_slug  TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_gaps_time ON idle_gaps(started_at);
        CREATE TABLE IF NOT EXISTS alerts (
            id           INTEGER PRIMARY KEY,
            session_id   INTEGER NOT NULL REFERENCES sessions(id),
            fired_at     INTEGER NOT NULL,
            trigger_kind TEXT    NOT NULL,
            gap_started  INTEGER NOT NULL,
            process      TEXT,
            domain       TEXT,
            response     TEXT,
            responded_at INTEGER,
            refocused_at INTEGER
        );
        CREATE TABLE IF NOT EXISTS session_apps (
            session_id    INTEGER NOT NULL REFERENCES sessions(id),
            process       TEXT    NOT NULL,
            first_seen    INTEGER NOT NULL,
            last_seen     INTEGER NOT NULL,
            open_seconds  REAL    NOT NULL,
            focus_seconds REAL    NOT NULL,
            list          TEXT    NOT NULL,
            PRIMARY KEY (session_id, process)
        );
        """;

    private static long Ms(DateTimeOffset t) => LocalTime.ToMs(t);
    private static long? Ms(DateTimeOffset? t) => t is { } v ? LocalTime.ToMs(v) : null;
    private DateTimeOffset At(long ms) => LocalTime.FromMs(ms, _zone);
    private DateTimeOffset? At(long? ms) => ms is { } v ? LocalTime.FromMs(v, _zone) : null;

    // ---------------------------------------------------------------- ISessionStore

    public long StartSession(string planSlug, string planName, DateTimeOffset at)
    {
        using var c = Open();
        return c.ExecuteScalar<long>(
            "INSERT INTO sessions (plan_slug, plan_name, started_at, last_heartbeat) VALUES (@planSlug, @planName, @at, @at); SELECT last_insert_rowid();",
            new { planSlug, planName, at = Ms(at) });
    }

    public void EndSession(long sessionId, DateTimeOffset at, string reason)
    {
        using var c = Open();
        c.Execute("UPDATE sessions SET ended_at = @at, end_reason = @reason, last_heartbeat = @at WHERE id = @sessionId AND ended_at IS NULL",
            new { sessionId, at = Ms(at), reason });
    }

    public void Heartbeat(long sessionId, DateTimeOffset at)
    {
        using var c = Open();
        c.Execute("UPDATE sessions SET last_heartbeat = @at WHERE id = @sessionId", new { sessionId, at = Ms(at) });
    }

    public void AddSegment(SegmentRecord s)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO focus_segments (session_id, started_at, ended_at, process, domain, title, list, plan_slug, topic, category, entry)
            VALUES (@SessionId, @Start, @End, @Process, @Domain, @Title, @List, @PlanSlug, @Topic, @Category, @Entry)
            """,
            new
            {
                s.SessionId, Start = Ms(s.Start), End = Ms(s.End), s.Process, s.Domain, s.Title, s.List, s.PlanSlug, s.Topic, s.Category, s.Entry,
            });
    }

    public void AddIdleGap(IdleGapRecord g)
    {
        using var c = Open();
        c.Execute("INSERT INTO idle_gaps (session_id, started_at, ended_at, label, plan_slug) VALUES (@SessionId, @Start, @End, @Label, @PlanSlug)",
            new { g.SessionId, Start = Ms(g.Start), End = Ms(g.End), g.Label, g.PlanSlug });
    }

    public long AddAlert(AlertRecord a)
    {
        using var c = Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO alerts (session_id, fired_at, trigger_kind, gap_started, process, domain, response, responded_at, refocused_at)
            VALUES (@SessionId, @FiredAt, @Trigger, @GapStarted, @Process, @Domain, @Response, @RespondedAt, @RefocusedAt);
            SELECT last_insert_rowid();
            """,
            new
            {
                a.SessionId, FiredAt = Ms(a.FiredAt), a.Trigger, GapStarted = Ms(a.GapStarted), a.Process, a.Domain, a.Response,
                RespondedAt = Ms(a.RespondedAt), RefocusedAt = Ms(a.RefocusedAt),
            });
    }

    public void UpdateAlert(long alertId, string? response, DateTimeOffset? respondedAt, DateTimeOffset? refocusedAt)
    {
        using var c = Open();
        c.Execute("""
            UPDATE alerts SET
                response     = COALESCE(@response, response),
                responded_at = COALESCE(@respondedAt, responded_at),
                refocused_at = COALESCE(@refocusedAt, refocused_at)
            WHERE id = @alertId
            """,
            new { alertId, response, respondedAt = Ms(respondedAt), refocusedAt = Ms(refocusedAt) });
    }

    public void UpsertSessionApps(long sessionId, IReadOnlyList<SessionAppRecord> apps)
    {
        if (apps.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var a in apps)
        {
            c.Execute("""
                INSERT INTO session_apps (session_id, process, first_seen, last_seen, open_seconds, focus_seconds, list)
                VALUES (@sessionId, @Process, @FirstSeen, @LastSeen, @OpenSeconds, @FocusSeconds, @List)
                ON CONFLICT (session_id, process) DO UPDATE SET
                    last_seen = excluded.last_seen, open_seconds = excluded.open_seconds,
                    focus_seconds = excluded.focus_seconds, list = excluded.list
                """,
                new { sessionId, a.Process, FirstSeen = Ms(a.FirstSeen), LastSeen = Ms(a.LastSeen), a.OpenSeconds, a.FocusSeconds, a.List },
                tx);
        }
        tx.Commit();
    }

    public TimeSpan SumPrep(string planSlug, DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        var ms = c.ExecuteScalar<long?>("""
            SELECT
              (SELECT COALESCE(SUM(MIN(ended_at, @to) - MAX(started_at, @from)), 0) FROM focus_segments
                WHERE list = 'focus' AND plan_slug = @planSlug AND ended_at > @from AND started_at < @to)
            + (SELECT COALESCE(SUM(MIN(ended_at, @to) - MAX(started_at, @from)), 0) FROM idle_gaps
                WHERE label = 'reading' AND plan_slug = @planSlug AND ended_at > @from AND started_at < @to)
            """,
            new { planSlug, from = Ms(from), to = Ms(to) }) ?? 0;
        return TimeSpan.FromMilliseconds(ms);
    }

    // ---------------------------------------------------------------- IReportSource

    public ReportData Load(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        var p = new { from = Ms(from), to = Ms(to) };

        var sessions = c.Query<SessionRow>("""
            SELECT id AS Id, plan_slug AS PlanSlug, plan_name AS PlanName, started_at AS StartedAt, ended_at AS EndedAt,
                   end_reason AS EndReason, last_heartbeat AS LastHeartbeat
            FROM sessions WHERE started_at < @to AND COALESCE(ended_at, @to) > @from ORDER BY started_at
            """, p)
            .Select(r => new SessionRecord(r.Id, r.PlanSlug, r.PlanName, At(r.StartedAt), At(r.EndedAt), r.EndReason, At(r.LastHeartbeat)))
            .ToList();

        var segments = c.Query<SegmentRow>("""
            SELECT session_id AS SessionId, started_at AS StartedAt, ended_at AS EndedAt, process AS Process, domain AS Domain, title AS Title,
                   list AS List, plan_slug AS PlanSlug, topic AS Topic, category AS Category, entry AS Entry
            FROM focus_segments WHERE started_at < @to AND ended_at > @from ORDER BY started_at
            """, p)
            .Select(r => new SegmentRecord(r.SessionId, At(r.StartedAt), At(r.EndedAt), r.Process, r.Domain, r.Title, r.List, r.PlanSlug, r.Topic, r.Category, r.Entry))
            .ToList();

        var gaps = c.Query<GapRow>("""
            SELECT session_id AS SessionId, started_at AS StartedAt, ended_at AS EndedAt, label AS Label, plan_slug AS PlanSlug
            FROM idle_gaps WHERE started_at < @to AND ended_at > @from ORDER BY started_at
            """, p)
            .Select(r => new IdleGapRecord(r.SessionId, At(r.StartedAt), At(r.EndedAt), r.Label, r.PlanSlug))
            .ToList();

        var alerts = c.Query<AlertRow>("""
            SELECT id AS Id, session_id AS SessionId, fired_at AS FiredAt, trigger_kind AS TriggerKind, gap_started AS GapStarted,
                   process AS Process, domain AS Domain, response AS Response, responded_at AS RespondedAt, refocused_at AS RefocusedAt
            FROM alerts WHERE fired_at >= @from AND fired_at < @to ORDER BY fired_at
            """, p)
            .Select(r => new AlertRecord(r.Id, r.SessionId, At(r.FiredAt), r.TriggerKind, At(r.GapStarted), r.Process, r.Domain, r.Response,
                At(r.RespondedAt), At(r.RefocusedAt)))
            .ToList();

        var ids = sessions.Select(s => s.Id).ToArray();
        var apps = ids.Length == 0
            ? new List<SessionAppRecord>()
            : c.Query<AppRow>("""
                SELECT session_id AS SessionId, process AS Process, first_seen AS FirstSeen, last_seen AS LastSeen,
                       open_seconds AS OpenSeconds, focus_seconds AS FocusSeconds, list AS List
                FROM session_apps WHERE session_id IN @ids
                """, new { ids })
                .Select(r => new SessionAppRecord(r.SessionId, r.Process, At(r.FirstSeen), At(r.LastSeen), r.OpenSeconds, r.FocusSeconds, r.List))
                .ToList();

        return new ReportData(sessions, segments, gaps, alerts, apps);
    }

    public string? LastPlanSlug()
    {
        using var c = Open();
        return c.ExecuteScalar<string?>("SELECT plan_slug FROM sessions ORDER BY id DESC LIMIT 1");
    }

    public int CloseUnfinishedSessions()
    {
        using var c = Open();
        return c.Execute("UPDATE sessions SET ended_at = COALESCE(last_heartbeat, started_at), end_reason = 'unexpected' WHERE ended_at IS NULL");
    }

    // Row shapes for Dapper (column aliases match property names).
    private sealed class SessionRow
    {
        public long Id { get; set; }
        public string PlanSlug { get; set; } = "";
        public string PlanName { get; set; } = "";
        public long StartedAt { get; set; }
        public long? EndedAt { get; set; }
        public string? EndReason { get; set; }
        public long? LastHeartbeat { get; set; }
    }

    private sealed class SegmentRow
    {
        public long SessionId { get; set; }
        public long StartedAt { get; set; }
        public long EndedAt { get; set; }
        public string Process { get; set; } = "";
        public string? Domain { get; set; }
        public string? Title { get; set; }
        public string List { get; set; } = "";
        public string? PlanSlug { get; set; }
        public string? Topic { get; set; }
        public string? Category { get; set; }
        public string? Entry { get; set; }
    }

    private sealed class GapRow
    {
        public long SessionId { get; set; }
        public long StartedAt { get; set; }
        public long EndedAt { get; set; }
        public string? Label { get; set; }
        public string? PlanSlug { get; set; }
    }

    private sealed class AlertRow
    {
        public long Id { get; set; }
        public long SessionId { get; set; }
        public long FiredAt { get; set; }
        public string TriggerKind { get; set; } = "";
        public long GapStarted { get; set; }
        public string? Process { get; set; }
        public string? Domain { get; set; }
        public string? Response { get; set; }
        public long? RespondedAt { get; set; }
        public long? RefocusedAt { get; set; }
    }

    private sealed class AppRow
    {
        public long SessionId { get; set; }
        public string Process { get; set; } = "";
        public long FirstSeen { get; set; }
        public long LastSeen { get; set; }
        public double OpenSeconds { get; set; }
        public double FocusSeconds { get; set; }
        public string List { get; set; } = "";
    }
}
