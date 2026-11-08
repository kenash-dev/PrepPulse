using PrepPulse.Core.Matching;

namespace PrepPulse.Core.Engine;

public enum SessionState { None, Running, Paused, Locked, OnBreak }

/// <summary>What the tray icon and taskbar show: green Focused, yellow Drifting/Idle, red FocusLost, grey Paused/OnBreak.</summary>
public enum FocusState { NoSession, Focused, Drifting, Idle, FocusLost, Paused, OnBreak }

public enum EndReason { Manual, Sleep, Shutdown, LockTimeout, Unexpected, AppExit }

public enum AlertTrigger { NoInput, Distraction, NeitherList }

public enum AlertResponse { Back, Reading, Break, End, Ignored }

/// <summary>One reading of the desktop, taken every second by the sampler.</summary>
public sealed record Sample(DateTimeOffset At, Observation? Foreground, TimeSpan IdleFor, bool OwnWindow = false);

public sealed record ActiveAlertInfo(long Id, AlertTrigger Trigger, DateTimeOffset FiredAt, DateTimeOffset Since, string? What, string? LastFocus, string PlanName, bool Acknowledged);

/// <summary>Immutable view of the engine for the UI. Published after every change.</summary>
public sealed record EngineSnapshot(
    SessionState Session,
    FocusState Focus,
    long? SessionId,
    string? PlanSlug,
    string? PlanName,
    DateTimeOffset? SessionStart,
    TimeSpan SessionLength,
    string? Current,
    ListKind? CurrentList,
    string? CurrentTopic,
    TimeSpan IdleFor,
    TimeSpan PrepToday,
    TimeSpan TargetToday,
    ActiveAlertInfo? Alert,
    DateTimeOffset? BreakUntil,
    string? LastFocus)
{
    public static readonly EngineSnapshot Empty = new(SessionState.None, FocusState.NoSession, null, null, null, null, TimeSpan.Zero,
        null, null, null, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null, null, null);

    public bool IsRunning => Session != SessionState.None;

    /// <summary>Today's prep as a fraction of today's target (0 when there's no target).</summary>
    public double TodayProgress => TargetToday > TimeSpan.Zero ? Math.Min(1.0, PrepToday / TargetToday) : 0;
}

public abstract record EngineEvent;
public sealed record SessionStarted(long SessionId, string PlanSlug, string PlanName) : EngineEvent;
public sealed record SessionEnded(long SessionId, EndReason Reason, DateTimeOffset At, string PlanName) : EngineEvent;
public sealed record AlertFired(ActiveAlertInfo Alert) : EngineEvent;
public sealed record AlertCleared(long AlertId, AlertResponse Response) : EngineEvent;
public sealed record BeepRequested(AlertTrigger Trigger) : EngineEvent;
public sealed record BreakOver : EngineEvent;
public sealed record PlanSuggested(string Slug, string Name, string What) : EngineEvent;
public sealed record OpenDistractionsFound(IReadOnlyList<string> Apps) : EngineEvent;

/// <summary>Short keys used in the database and API.</summary>
public static class Keys
{
    public static string Of(EndReason r) => r switch
    {
        EndReason.Manual => "manual",
        EndReason.Sleep => "sleep",
        EndReason.Shutdown => "shutdown",
        EndReason.LockTimeout => "lock_timeout",
        EndReason.Unexpected => "unexpected",
        _ => "app_exit",
    };

    public static string Of(AlertTrigger t) => t switch
    {
        AlertTrigger.NoInput => "no_input",
        AlertTrigger.Distraction => "distraction",
        _ => "neither_list",
    };

    public static string Of(AlertResponse r) => r switch
    {
        AlertResponse.Back => "back",
        AlertResponse.Reading => "reading",
        AlertResponse.Break => "break",
        AlertResponse.End => "end",
        _ => "ignored",
    };

    public static string Of(ListKind l) => l switch
    {
        ListKind.Focus => "focus",
        ListKind.Distraction => "distraction",
        _ => "neither",
    };

    public static string EndReasonLabel(string? key) => key switch
    {
        null => "Running",
        "manual" => "Stop",
        "sleep" => "PC went to sleep",
        "shutdown" => "Restart or shutdown",
        "lock_timeout" => "Locked too long",
        "unexpected" => "Unexpected (crash or power loss)",
        "app_exit" => "PrepPulse closed",
        _ => key,
    };

    public static string TriggerLabel(string key) => key switch
    {
        "no_input" => "No input",
        "distraction" => "Distraction",
        "neither_list" => "Off plan",
        _ => key,
    };

    public static string ResponseLabel(string? key) => key switch
    {
        null => "–",
        "back" => "Back to it",
        "reading" => "I'm reading",
        "break" => "Break",
        "end" => "End session",
        "ignored" => "No answer",
        _ => key,
    };
}
