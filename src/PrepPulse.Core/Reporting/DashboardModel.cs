namespace PrepPulse.Core.Reporting;

// The JSON the dashboard page renders (camelCase on the wire). Minutes and hours are doubles; dates are yyyy-MM-dd.

public sealed record DashboardModel(
    string GeneratedAt,
    string Today,
    string? Selected,
    IReadOnlyList<PlanSummary> Plans,
    TodayTiles Tiles,
    IReadOnlyList<TimelineSession> Timeline,
    IReadOnlyList<TimeUse> TimeUse,
    IReadOnlyList<SessionRow> SessionsToday,
    IReadOnlyList<DayTotal> Days,
    BurnUpModel? BurnUp,
    IReadOnlyList<WeekTotal> Weeks,
    IReadOnlyList<FocusRatioDay> FocusRatio,
    OpenAppsModel? Apps,
    IReadOnlyList<AlertRow> Alerts,
    IReadOnlyList<TopicProgress> Topics,
    LiveModel? Live,
    IReadOnlyList<string> PlanErrors);

public sealed record PlanSummary(
    string Slug, string Name, string Colour, string Start, string? End, int? DaysLeft, string Targets,
    double DoneHours, double? PlanHours, double TodayMinutes, double TodayTargetMinutes,
    double WeekMinutes, double WeekTargetMinutes, double? PacePercent, double? BehindHours, bool Paused, int TopicCount);

public sealed record TodayTiles(
    double PrepMinutes, double TargetMinutes, int Sessions, double SessionMinutes, double? FocusRatio,
    double LongestBlockMinutes, int IdlePauses, double IdleMinutes, double? PacePercent, double? BehindHours, int Streak);

public sealed record TimelineSession(int N, string Plan, double Start, double End, string EndedBy, bool Live,
    IReadOnlyList<TimelineBlock> Blocks, IReadOnlyList<TimelineAlert> Alerts);

/// <summary>Kind: coding, design, ai, mock, other (focus categories), distraction, neither, idle, reading, break, paused, locked.</summary>
public sealed record TimelineBlock(double Start, double End, string Kind, string Label, string? Title, string? Topic);

public sealed record TimelineAlert(double At, string Trigger, string? What, string Response);

public sealed record TimeUse(string Label, double Minutes, string Kind);

public sealed record SessionRow(int N, string Plan, string Start, string Stop, double Minutes, double PrepMinutes,
    double IdleMinutes, int IdleCount, double DistractionMinutes, double? FocusPercent, string EndedBy);

public sealed record DayTotal(string Date, IReadOnlyDictionary<string, double> ByCategory, double Total, double Target);

public sealed record BurnUpModel(string Plan, string Start, string End, int TodayIndex,
    IReadOnlyList<double> Planned, IReadOnlyList<double?> Actual, IReadOnlyList<double?> Projected);

public sealed record WeekTotal(string WeekStart, double Minutes, double TargetMinutes);

public sealed record FocusRatioDay(string Date, double? Ratio);

public sealed record OpenAppsModel(int SessionN, string Date, string Start, string Stop, string EndedBy, IReadOnlyList<OpenAppRow> Rows);

public sealed record OpenAppRow(string Name, string List, double OpenMinutes, double FocusMinutes, bool Child, string? Note);

public sealed record AlertRow(string Time, string Trigger, string? What, string Response, double? BackAfterMinutes);

public sealed record TopicProgress(string Plan, string Name, string Category, double DoneHours, double? DueHours, double? TotalHours);

public sealed record LiveModel(string State, string Focus, string? Plan, string? Current, double SessionMinutes, double PrepTodayMinutes, double TargetTodayMinutes);
