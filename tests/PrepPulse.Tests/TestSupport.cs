using PrepPulse.Core;
using PrepPulse.Core.Engine;
using PrepPulse.Core.Matching;
using PrepPulse.Core.Plans;

namespace PrepPulse.Tests;

/// <summary>A clock fixed in a +11:00 zone (Sydney in summer), moved by hand.</summary>
public sealed class FakeClock : IClock
{
    public static readonly TimeZoneInfo Sydney = TimeZoneInfo.CreateCustomTimeZone("Test+11", TimeSpan.FromHours(11), "Test+11", "Test+11");

    public FakeClock(DateTimeOffset start) => Now = start;

    public DateTimeOffset Now { get; set; }
    public TimeZoneInfo Zone => Sydney;

    public void Advance(TimeSpan by) => Now += by;

    public static DateTimeOffset At(int year, int month, int day, int hour, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.FromHours(11));
}

/// <summary>In-memory store that records everything the engine writes.</summary>
public sealed class FakeStore : ISessionStore
{
    private long _nextId = 1;

    public List<SessionRecord> Sessions { get; } = [];
    public List<SegmentRecord> Segments { get; } = [];
    public List<IdleGapRecord> Gaps { get; } = [];
    public List<AlertRecord> Alerts { get; } = [];
    public Dictionary<(long, string), SessionAppRecord> Apps { get; } = new();
    public int Heartbeats { get; private set; }

    public long StartSession(string planSlug, string planName, DateTimeOffset at)
    {
        var id = _nextId++;
        Sessions.Add(new SessionRecord(id, planSlug, planName, at, null, null, at));
        return id;
    }

    public void EndSession(long sessionId, DateTimeOffset at, string reason)
    {
        var i = Sessions.FindIndex(s => s.Id == sessionId);
        Sessions[i] = Sessions[i] with { End = at, EndReason = reason };
    }

    public void Heartbeat(long sessionId, DateTimeOffset at) => Heartbeats++;
    public void AddSegment(SegmentRecord segment) => Segments.Add(segment);
    public void AddIdleGap(IdleGapRecord gap) => Gaps.Add(gap);

    public long AddAlert(AlertRecord alert)
    {
        var id = _nextId++;
        Alerts.Add(alert with { Id = id });
        return id;
    }

    public void UpdateAlert(long alertId, string? response, DateTimeOffset? respondedAt, DateTimeOffset? refocusedAt)
    {
        var i = Alerts.FindIndex(a => a.Id == alertId);
        var a = Alerts[i];
        Alerts[i] = a with
        {
            Response = response ?? a.Response,
            RespondedAt = respondedAt ?? a.RespondedAt,
            RefocusedAt = refocusedAt ?? a.RefocusedAt,
        };
    }

    public void UpsertSessionApps(long sessionId, IReadOnlyList<SessionAppRecord> apps)
    {
        foreach (var a in apps) Apps[(sessionId, a.Process)] = a;
    }

    public TimeSpan SumPrep(string planSlug, DateTimeOffset from, DateTimeOffset to)
    {
        var total = TimeSpan.Zero;
        foreach (var s in Segments.Where(s => s.List == "focus" && s.PlanSlug == planSlug)) total += LocalTime.Overlap(s.Start, s.End, from, to);
        foreach (var g in Gaps.Where(g => g.Label == "reading" && g.PlanSlug == planSlug)) total += LocalTime.Overlap(g.Start, g.End, from, to);
        return total;
    }
}

public static class TestPlans
{
    public const string InterviewYaml = """
        name: Interview prep
        start: 2026-11-02
        end: 2026-12-18
        targets:
          weekdays: 2h
          weekends: 4h
          days_off: [2026-11-29]
          overrides:
            2026-12-13: 6h
        topics:
          - name: Graphs
            category: coding
            hours: 18
            weeks: 1-3
            focus: [leetcode.com, notepad.exe]
          - name: Dynamic programming
            category: coding
            hours: 18
            weeks: 4-7
            focus: [leetcode.com]
          - name: System design
            category: design
            hours: 24
            focus: [bytebytego.com]
            passive: ["youtube.com | system design"]
        """;

    public const string LearningYaml = """
        name: Learning AI
        start: 2026-10-01
        targets:
          weekly: 7h
        topics:
          - name: Fine-tuning
            category: ai
            hours: 10
            focus: [huggingface.co]
        """;

    public static Plan Interview() => PlanLoader.Parse(InterviewYaml, "interview-prep");
    public static Plan Learning() => PlanLoader.Parse(LearningYaml, "learning-ai");

    public static IReadOnlyList<FocusEntry> Distractions() =>
        PlanLoader.ParseDistractions("distractions: [WhatsApp.exe, mspaint.exe, reddit.com, youtube.com]");

    public static PlanCatalogSnapshot Catalog() => new()
    {
        Plans = [Interview(), Learning()],
        Distractions = Distractions(),
    };

    public static Observation App(string process, string title = "") => new(process, title, null, false, false);
    public static Observation Site(string domain, string title = "") => new("chrome.exe", title, domain, true, false);
}

/// <summary>Engine wired to fakes, with helpers to simulate seconds passing.</summary>
public sealed class EngineHarness
{
    public FakeClock Clock { get; }
    public FakeStore Store { get; } = new();
    public AppSettings Settings { get; } = new();
    public PlanCatalogSnapshot Catalog { get; set; } = TestPlans.Catalog();
    public FocusEngine Engine { get; }
    public List<EngineEvent> Events { get; } = [];
    private DateTimeOffset _lastInput;

    public EngineHarness(DateTimeOffset? start = null)
    {
        Clock = new FakeClock(start ?? FakeClock.At(2026, 11, 9, 9, 0));
        Engine = new FocusEngine(Clock, Store, () => Settings, () => Catalog);
        Engine.Event += Events.Add;
        _lastInput = Clock.Now;
    }

    /// <summary>Spend <paramref name="seconds"/> on <paramref name="what"/>, ticking once a second, with or without typing.</summary>
    public void Spend(int seconds, Observation? what, bool typing = true)
    {
        for (var i = 0; i < seconds; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            if (typing) _lastInput = Clock.Now;
            Engine.Tick(new Sample(Clock.Now, what, Clock.Now - _lastInput));
        }
    }

    public void SpendMinutes(double minutes, Observation? what, bool typing = true) => Spend((int)(minutes * 60), what, typing);

    public T? Last<T>() where T : EngineEvent => Events.OfType<T>().LastOrDefault();
}
