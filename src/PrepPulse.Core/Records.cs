namespace PrepPulse.Core;

// What gets stored. Lists, triggers, responses and end reasons are stored as short keys (see Engine.Keys).

public sealed record SessionRecord(long Id, string PlanSlug, string PlanName, DateTimeOffset Start, DateTimeOffset? End, string? EndReason, DateTimeOffset? LastHeartbeat);

public sealed record SegmentRecord(long SessionId, DateTimeOffset Start, DateTimeOffset End, string Process, string? Domain, string? Title,
    string List, string? PlanSlug, string? Topic, string? Category, string? Entry);

public sealed record IdleGapRecord(long SessionId, DateTimeOffset Start, DateTimeOffset End, string? Label, string? PlanSlug);

public sealed record AlertRecord(long Id, long SessionId, DateTimeOffset FiredAt, string Trigger, DateTimeOffset GapStarted, string? Process, string? Domain,
    string? Response, DateTimeOffset? RespondedAt, DateTimeOffset? RefocusedAt);

public sealed record SessionAppRecord(long SessionId, string Process, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, double OpenSeconds, double FocusSeconds, string List);

public sealed record ReportData(
    IReadOnlyList<SessionRecord> Sessions,
    IReadOnlyList<SegmentRecord> Segments,
    IReadOnlyList<IdleGapRecord> Gaps,
    IReadOnlyList<AlertRecord> Alerts,
    IReadOnlyList<SessionAppRecord> Apps)
{
    public static readonly ReportData Empty = new([], [], [], [], []);
}

/// <summary>Write side used by the engine. Implemented by the SQLite store; tests use an in-memory fake.</summary>
public interface ISessionStore
{
    long StartSession(string planSlug, string planName, DateTimeOffset at);
    void EndSession(long sessionId, DateTimeOffset at, string reason);
    void Heartbeat(long sessionId, DateTimeOffset at);
    void AddSegment(SegmentRecord segment);
    void AddIdleGap(IdleGapRecord gap);
    long AddAlert(AlertRecord alert);
    void UpdateAlert(long alertId, string? response, DateTimeOffset? respondedAt, DateTimeOffset? refocusedAt);
    void UpsertSessionApps(long sessionId, IReadOnlyList<SessionAppRecord> apps);
    /// <summary>Focus time (plus pauses marked as reading) for a plan between two instants.</summary>
    TimeSpan SumPrep(string planSlug, DateTimeOffset from, DateTimeOffset to);
}

/// <summary>Read side used by the dashboard.</summary>
public interface IReportSource
{
    ReportData Load(DateTimeOffset from, DateTimeOffset to);
    string? LastPlanSlug();
    /// <summary>Closes sessions left open by a crash or power loss at their last heartbeat. Returns how many.</summary>
    int CloseUnfinishedSessions();
}
