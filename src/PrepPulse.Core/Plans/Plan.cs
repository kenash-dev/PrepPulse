namespace PrepPulse.Core.Plans;

public enum Category { Coding, Design, Ai, Mock, Other }

public static class Categories
{
    public static Category Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "coding" or "code" or "coding practice" or "dsa" or "leetcode" => Category.Coding,
        "design" or "system design" or "system-design" or "systems" => Category.Design,
        "ai" or "ml" or "ai/ml" or "ai / ml" or "ai-ml" or "ai / ml study" or "llm" => Category.Ai,
        "mock" or "mocks" or "behavioral" or "behavioural" or "mock & behavioral" or "interview" => Category.Mock,
        _ => Category.Other,
    };

    public static string Key(Category c) => c switch
    {
        Category.Coding => "coding",
        Category.Design => "design",
        Category.Ai => "ai",
        Category.Mock => "mock",
        _ => "other",
    };

    public static string Label(Category c) => c switch
    {
        Category.Coding => "Coding practice",
        Category.Design => "System design",
        Category.Ai => "AI / ML study",
        Category.Mock => "Mock & behavioral",
        _ => "Other study",
    };
}

public enum EntryKind { App, Site }

/// <summary>
/// One line of a focus or distraction list: an app by program name (Code.exe) or a website by domain (leetcode.com),
/// optionally narrowed by words in the window title ("youtube.com | system design").
/// </summary>
public sealed record FocusEntry(string Raw, EntryKind Kind, string Pattern, string? TitleContains, bool PassiveOk)
{
    public static FocusEntry Parse(string raw, bool passiveOk = false)
    {
        var text = raw.Trim();
        string? title = null;
        var bar = text.IndexOf('|');
        if (bar >= 0)
        {
            title = text[(bar + 1)..].Trim();
            text = text[..bar].Trim();
            if (title.Length == 0) title = null;
        }

        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return new FocusEntry(raw.Trim(), EntryKind.App, Path.GetFileName(text).ToLowerInvariant(), title, passiveOk);

        return new FocusEntry(raw.Trim(), EntryKind.Site, NormaliseSite(text), title, passiveOk);
    }

    public static string NormaliseSite(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        var scheme = t.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) t = t[(scheme + 3)..];
        var slash = t.IndexOf('/');
        if (slash >= 0) t = t[..slash];
        if (t.StartsWith("www.", StringComparison.Ordinal)) t = t[4..];
        return t;
    }

    public string Display => TitleContains is null ? Pattern : $"{Pattern} | {TitleContains}";
}

public sealed class Topic
{
    public required string Name { get; init; }
    public Category Category { get; init; } = Category.Other;
    public double? Hours { get; init; }
    public int? WeekFrom { get; init; }
    public int? WeekTo { get; init; }
    public IReadOnlyList<FocusEntry> Entries { get; init; } = [];

    public bool IsScheduled(int week) =>
        (WeekFrom is null || week >= WeekFrom) && (WeekTo is null || week <= WeekTo);
}

/// <summary>A study plan loaded from a YAML file in the plans folder. Immutable; a reload creates new instances.</summary>
public sealed class Plan
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string Colour { get; init; } = "blue";
    public DateOnly Start { get; init; }
    public DateOnly? End { get; init; }
    public bool Paused { get; init; }
    public TimeSpan? Weekday { get; init; }
    public TimeSpan? Weekend { get; init; }
    public TimeSpan? Weekly { get; init; }
    public IReadOnlySet<DateOnly> DaysOff { get; init; } = new HashSet<DateOnly>();
    public IReadOnlyDictionary<DateOnly, TimeSpan> Overrides { get; init; } = new Dictionary<DateOnly, TimeSpan>();
    public IReadOnlyList<Topic> Topics { get; init; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<string>> Weeks { get; init; } = new Dictionary<int, IReadOnlyList<string>>();
    public string FilePath { get; init; } = "";

    public bool IsOpenEnded => End is null;

    /// <summary>Week 1 is the seven days starting on the plan's start date.</summary>
    public int WeekOf(DateOnly d) => d < Start ? 0 : (d.DayNumber - Start.DayNumber) / 7 + 1;

    public bool IsActiveOn(DateOnly d) => d >= Start && (End is null || d <= End);

    /// <summary>Expected study time on a day. Weekly targets are spread evenly so daily and cumulative charts work.</summary>
    public TimeSpan ExpectedOn(DateOnly d)
    {
        if (Paused || !IsActiveOn(d) || DaysOff.Contains(d)) return TimeSpan.Zero;
        if (Overrides.TryGetValue(d, out var o)) return o;
        if (Weekly is { } w) return w / 7;
        var weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return (weekend ? Weekend ?? Weekday : Weekday ?? Weekend) ?? TimeSpan.Zero;
    }

    public TimeSpan ExpectedBetween(DateOnly from, DateOnly to)
    {
        var total = TimeSpan.Zero;
        for (var d = from; d <= to; d = d.AddDays(1)) total += ExpectedOn(d);
        return total;
    }

    public string TargetSummary => Weekly is { } w
        ? $"{Durations.Format(w)} a week"
        : $"{Durations.Format(Weekday ?? TimeSpan.Zero)} weekdays, {Durations.Format(Weekend ?? Weekday ?? TimeSpan.Zero)} weekends";

    public double? TotalTopicHours => Topics.Any(t => t.Hours.HasValue) ? Topics.Sum(t => t.Hours ?? 0) : null;

    public string ColourHex => Colour.Trim().ToLowerInvariant() switch
    {
        var c when c.StartsWith('#') => c,
        "blue" => "#2a78d6",
        "orange" => "#eb6834",
        "teal" or "aqua" or "green" => "#1baf7a",
        "yellow" or "amber" => "#eda100",
        "pink" or "magenta" => "#e87ba4",
        "violet" or "purple" => "#6250d6",
        "red" => "#e34948",
        _ => "#2a78d6",
    };
}
