using PrepPulse.Core.Matching;
using PrepPulse.Core.Plans;

namespace PrepPulse.Core.Engine;

/// <summary>
/// The session state machine. It is fed one <see cref="Sample"/> a second plus lifecycle events (lock, sleep, shutdown,
/// user commands), turns them into focus segments, idle gaps and alerts, writes them through <see cref="ISessionStore"/>,
/// and publishes an <see cref="EngineSnapshot"/> for the UI. It never touches Windows APIs, so it is fully unit-testable.
/// Thread-safe: every public member takes one lock; events are raised after the lock is released.
/// </summary>
public sealed class FocusEngine
{
    public static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly ISessionStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly Func<PlanCatalogSnapshot> _catalog;

    private ActiveSession? _s;
    private EngineSnapshot _snapshot = EngineSnapshot.Empty;

    public FocusEngine(IClock clock, ISessionStore store, Func<AppSettings> settings, Func<PlanCatalogSnapshot> catalog)
    {
        _clock = clock;
        _store = store;
        _settings = settings;
        _catalog = catalog;
    }

    /// <summary>Raised (outside the lock) for things the UI should react to once: alerts, beeps, session start/end.</summary>
    public event Action<EngineEvent>? Event;

    /// <summary>Raised (outside the lock) after every tick or command with the latest snapshot.</summary>
    public event Action<EngineSnapshot>? Changed;

    public EngineSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public EndReason? LastEndReason { get; private set; }
    public DateTimeOffset? LastEndedAt { get; private set; }
    public string? LastPlanName { get; private set; }

    // ------------------------------------------------------------------ commands

    public bool StartSession(string planSlug)
    {
        return Run(ev =>
        {
            if (_s is not null) return false;
            var plan = _catalog().Find(planSlug) ?? throw new InvalidOperationException($"There's no plan called '{planSlug}'.");
            var now = _clock.Now;
            var id = _store.StartSession(plan.Slug, plan.Name, now);
            var day = LocalTime.DateOf(now, _clock.Zone);
            _s = new ActiveSession
            {
                Id = id, Plan = plan, Start = now, LastTick = now, LastHeartbeat = now, Day = day,
                PrepClosedToday = PrepFromStore(plan, day),
            };
            ev.Add(new SessionStarted(id, plan.Slug, plan.Name));
            UpdateSnapshot(now);
            return true;
        });
    }

    public void StopSession(EndReason reason = EndReason.Manual) => Run(ev =>
    {
        if (_s is not null) End(_clock.Now, reason, ev);
        return true;
    });

    public void SwitchPlan(string planSlug) => Run(ev =>
    {
        var s = _s;
        if (s is null) return false;
        var plan = _catalog().Find(planSlug);
        if (plan is null || plan.Slug == s.Plan.Slug) return false;
        var now = _clock.Now;
        CloseSegment(s, now);
        s.Plan = plan;
        s.PrepClosedToday = PrepFromStore(plan, s.Day);
        s.Current = null;
        s.Suggestion = null;
        ResetRuns(s);
        UpdateSnapshot(now);
        return true;
    });

    public void Pause() => Run(ev => { if (_s is { State: SessionState.Running } s) Suspend(s, _clock.Now, SessionState.Paused, "paused", ev); return true; });

    public void Resume() => Run(ev =>
    {
        if (_s is { State: SessionState.Paused or SessionState.Locked } s) Unsuspend(s, _clock.Now);
        return true;
    });

    public void StartBreak() => Run(ev =>
    {
        if (_s is { State: SessionState.Running } s) BeginBreak(s, _clock.Now, ev);
        return true;
    });

    public void EndBreak() => Run(ev =>
    {
        if (_s is { State: SessionState.OnBreak } s)
        {
            var now = _clock.Now;
            CloseGap(s, now);
            s.State = SessionState.Running;
            s.BreakUntil = null;
            ResetRuns(s);
            UpdateSnapshot(now);
        }
        return true;
    });

    /// <summary>The user's answer to an alert popup.</summary>
    public void Respond(AlertResponse response) => Run(ev =>
    {
        var s = _s;
        if (s is null) return false;
        var now = _clock.Now;
        if (s.Alert is not { } a)
        {
            if (response == AlertResponse.End) End(now, EndReason.Manual, ev);
            if (response == AlertResponse.Break && s.State == SessionState.Running) BeginBreak(s, now, ev);
            return true;
        }

        switch (response)
        {
            case AlertResponse.Back:
                a.Response = AlertResponse.Back;
                a.RespondedAt = now;
                _store.UpdateAlert(a.Id, Keys.Of(AlertResponse.Back), now, null);
                break;
            case AlertResponse.Reading:
                if (s.Gap is { Label: null }) s.ReadingGap = true;
                ClearAlert(s, now, refocused: false, AlertResponse.Reading, ev);
                break;
            case AlertResponse.Break:
                ClearAlert(s, now, refocused: false, AlertResponse.Break, ev);
                BeginBreak(s, now, ev);
                break;
            case AlertResponse.End:
                ClearAlert(s, now, refocused: false, AlertResponse.End, ev);
                End(now, EndReason.Manual, ev);
                return true;
        }
        UpdateSnapshot(now);
        return true;
    });

    // ------------------------------------------------------------------ lifecycle events from Windows

    public void OnLock() => Run(ev =>
    {
        if (_s is { State: SessionState.Running or SessionState.OnBreak } s) Suspend(s, _clock.Now, SessionState.Locked, "locked", ev);
        return true;
    });

    public void OnUnlock() => Run(ev =>
    {
        if (_s is { State: SessionState.Locked } s)
        {
            var now = _clock.Now;
            var limit = TimeSpan.FromMinutes(_settings().Session.EndAfterLockedMinutes);
            if (s.PausedAt is { } at && now - at >= limit) End(at, EndReason.LockTimeout, ev);
            else Unsuspend(s, now);
        }
        return true;
    });

    public void OnSuspend() => Run(ev => { if (_s is not null) End(_clock.Now, EndReason.Sleep, ev); return true; });

    public void OnShutdown() => Run(ev => { if (_s is not null) End(_clock.Now, EndReason.Shutdown, ev); return true; });

    // ------------------------------------------------------------------ sampling

    public void Tick(Sample x) => Run(ev =>
    {
        if (_s is { } s) TickSession(s, x, ev);
        return true;
    });

    /// <summary>Programs with a visible window right now (lower-case file names). Called every ~30 s while a session runs.</summary>
    public void OnOpenWindows(IReadOnlyCollection<string> processes) => Run(ev =>
    {
        var s = _s;
        if (s is null) return false;
        var now = _clock.Now;
        var cfg = _settings();
        var dt = s.LastAppsSnapshot is { } prev ? Math.Clamp((now - prev).TotalSeconds, 0, 60) : 0;
        foreach (var p in processes)
        {
            var app = GetApp(s, p, now);
            app.LastSeen = now;
            app.OpenSeconds += dt;
        }
        foreach (var (name, app) in s.Apps) app.List = AppListKey(name, s.Plan);
        s.LastAppsSnapshot = now;
        _store.UpsertSessionApps(s.Id, AppRecords(s));

        if (!s.RemindedDistractions)
        {
            s.RemindedDistractions = true;
            if (cfg.Alerts.RemindOpenDistractionsAtStart)
            {
                var open = processes.Where(p => AppListKey(p, s.Plan) == "distraction")
                    .Select(Observation.ProcessDisplay).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (open.Count > 0) ev.Add(new OpenDistractionsFound(open));
            }
        }
        return true;
    });

    /// <summary>The still-open segment and gap, so the dashboard can show the current session live.</summary>
    public (IReadOnlyList<SegmentRecord> Segments, IReadOnlyList<IdleGapRecord> Gaps, long? SessionId) Live()
    {
        lock (_gate)
        {
            var s = _s;
            if (s is null) return ([], [], null);
            var now = _clock.Now;
            var segs = new List<SegmentRecord>();
            var gaps = new List<IdleGapRecord>();
            if (s.Segment is { } seg && now > seg.Start) segs.Add(ToRecord(s, seg, now));
            if (s.Gap is { } g && now > g.Start) gaps.Add(new IdleGapRecord(s.Id, g.Start, now, g.Label ?? (s.ReadingGap ? "reading" : null), s.Plan.Slug));
            return (segs, gaps, s.Id);
        }
    }

    // ------------------------------------------------------------------ core

    private void TickSession(ActiveSession s, Sample x, List<EngineEvent> ev)
    {
        var now = x.At;
        var cfg = _settings();

        // 1. A long gap between ticks means the PC slept (Modern Standby doesn't always send a suspend event).
        if (s.LastTick is { } last && now - last > SleepGap)
        {
            End(last, EndReason.Sleep, ev);
            return;
        }
        s.LastTick = now;

        // 2. Pick up edits to the plan file; roll over at midnight.
        if (_catalog().Find(s.Plan.Slug) is { } fresh && !ReferenceEquals(fresh, s.Plan)) s.Plan = fresh;
        var today = LocalTime.DateOf(now, _clock.Zone);
        if (today != s.Day)
        {
            s.Day = today;
            s.PrepClosedToday = PrepFromStore(s.Plan, today);
        }

        // 3. Heartbeat, so a crash or power cut can be closed at the right time on next start.
        if (now - s.LastHeartbeat >= HeartbeatEvery)
        {
            _store.Heartbeat(s.Id, now);
            s.LastHeartbeat = now;
        }

        // 4. Paused or locked: nothing to record, but a long lock ends the session at the moment of locking.
        if (s.State is SessionState.Paused or SessionState.Locked)
        {
            if (s.State == SessionState.Locked && s.PausedAt is { } lockedAt &&
                now - lockedAt >= TimeSpan.FromMinutes(cfg.Session.EndAfterLockedMinutes))
            {
                End(lockedAt, EndReason.LockTimeout, ev);
                return;
            }
            UpdateSnapshot(now, x.IdleFor);
            return;
        }

        // 5. Break: wait for it to run out.
        if (s.State == SessionState.OnBreak)
        {
            if (s.BreakUntil is { } until && now >= until)
            {
                CloseGap(s, until);
                s.State = SessionState.Running;
                s.BreakUntil = null;
                ResetRuns(s);
                ev.Add(new BreakOver());
            }
            else
            {
                UpdateSnapshot(now, x.IdleFor);
                return;
            }
        }

        // 6. Classify what's in focus. Our own windows (alert popup, picker) don't count; keep the previous answer.
        if (!x.OwnWindow && x.Foreground is { } obs)
        {
            s.CurrentObs = obs;
            s.Current = Classifier.Classify(obs, s.Plan, _catalog().Distractions, today);
        }
        var m = s.Current;
        var lastInput = now - x.IdleFor;
        var idleAfter = TimeSpan.FromMinutes(cfg.IdleAfterMinutes);
        DateTimeOffset? openSegmentAt = null;

        // 7. Idle gaps start at the last input (backdated), not when the threshold is crossed.
        if (s.Gap is null && x.IdleFor >= idleAfter && m?.Passive != true)
        {
            // Never reach back past the last recorded boundary, so segments and pauses never overlap.
            var floor = s.LastBoundary is { } b && b > s.Start ? b : s.Start;
            var gapStart = lastInput > floor ? lastInput : floor;
            CloseSegment(s, gapStart);
            s.Gap = new OpenGap { Start = gapStart };
        }
        else if (s.Gap is { Label: null } gap && x.IdleFor < idleAfter)
        {
            var resumed = lastInput > gap.Start ? lastInput : now;
            CloseGap(s, resumed);
            openSegmentAt = resumed;
        }
        var idle = s.Gap is not null;

        // 8. Segments: one per continuous stretch on the same app/site/list/topic.
        if (!idle && m is not null && s.CurrentObs is { } current)
        {
            var key = SegmentKey(current, m);
            if (s.Segment is null)
            {
                s.Segment = new OpenSegment { Start = openSegmentAt ?? now, Obs = current, Match = m, Key = key };
            }
            else if (s.Segment.Key != key)
            {
                CloseSegment(s, now);
                s.Segment = new OpenSegment { Start = now, Obs = current, Match = m, Key = key };
            }
            if (m.List == ListKind.Focus) s.LastFocusDisplay = FocusDisplay(current);
        }

        // 9. Alerts, 10. plan suggestions.
        EvaluateAlerts(s, x, m, idle, lastInput, cfg, ev);
        EvaluateSuggestion(s, now, m, idle, cfg, today, ev);
        UpdateSnapshot(now, x.IdleFor);
    }

    private void EvaluateAlerts(ActiveSession s, Sample x, Classification? m, bool idle, DateTimeOffset lastInput, AppSettings cfg, List<EngineEvent> ev)
    {
        var now = x.At;
        // What's in front, whether or not anyone is typing: watching a video on a distraction is still distraction.
        var current = m?.List;

        if (current == ListKind.Distraction) s.DistractionSince ??= now;
        else s.DistractionSince = null;
        if (current is ListKind.Distraction or ListKind.Neither) s.OffTaskSince ??= now;
        else s.OffTaskSince = null;

        if (s.Alert is { } a)
        {
            if (!idle && current == ListKind.Focus)
            {
                ClearAlert(s, now, refocused: true, null, ev);
                return;
            }
            var snd = cfg.Alerts.Sound;
            if (snd.Enabled && a.Response is null && !(snd.DistractionsOnly && a.Trigger == AlertTrigger.NoInput) &&
                a.Beeps < 1 + Math.Max(0, snd.MaxRepeats) &&
                now - a.LastBeep >= TimeSpan.FromMinutes(Math.Max(0.05, snd.RepeatEveryMinutes)))
            {
                a.Beeps++;
                a.LastBeep = now;
                ev.Add(new BeepRequested(a.Trigger));
            }
            return;
        }

        // Order matters when thresholds overlap: a distraction in front beats "no input", which beats "not in the plan".
        var al = cfg.Alerts;
        if (s.DistractionSince is { } ds && al.DistractionMinutes > 0 && now - ds >= TimeSpan.FromMinutes(al.DistractionMinutes))
        {
            Fire(s, AlertTrigger.Distraction, ds, now, cfg, ev);
            return;
        }
        if (idle && !s.ReadingGap && al.NoInputMinutes > 0 && x.IdleFor >= TimeSpan.FromMinutes(al.NoInputMinutes))
        {
            Fire(s, AlertTrigger.NoInput, lastInput, now, cfg, ev);
            return;
        }
        if (s.OffTaskSince is { } os && current == ListKind.Neither && al.NeitherListMinutes > 0 &&
            now - os >= TimeSpan.FromMinutes(al.NeitherListMinutes))
        {
            Fire(s, AlertTrigger.NeitherList, os, now, cfg, ev);
        }
    }

    private void EvaluateSuggestion(ActiveSession s, DateTimeOffset now, Classification? m, bool idle, AppSettings cfg, DateOnly today, List<EngineEvent> ev)
    {
        if (idle || m is null || m.List != ListKind.Neither || s.CurrentObs is null)
        {
            s.Suggestion = null;
            return;
        }
        var catalog = _catalog();
        var other = Classifier.OtherPlanFor(s.CurrentObs, catalog.Plans, s.Plan, catalog.Distractions, today);
        if (other is null) { s.Suggestion = null; return; }
        if (s.Suggestion is not { } sg || sg.Slug != other.Slug)
        {
            s.Suggestion = new Suggestion(other.Slug, now, false);
            return;
        }
        if (!sg.Sent && now - sg.Since >= TimeSpan.FromMinutes(cfg.Alerts.SuggestPlanAfterMinutes))
        {
            s.Suggestion = sg with { Sent = true };
            ev.Add(new PlanSuggested(other.Slug, other.Name, s.CurrentObs.Display));
        }
    }

    private void Fire(ActiveSession s, AlertTrigger trigger, DateTimeOffset since, DateTimeOffset now, AppSettings cfg, List<EngineEvent> ev)
    {
        var obs = s.CurrentObs;
        var id = _store.AddAlert(new AlertRecord(0, s.Id, now, Keys.Of(trigger), since, obs?.Process, obs?.Domain, null, null, null));
        var alert = new OpenAlert
        {
            Id = id, Trigger = trigger, FiredAt = now, Since = since, LastBeep = now,
            What = obs?.Display, LastFocus = s.LastFocusDisplay,
        };
        s.Alert = alert;
        var snd = cfg.Alerts.Sound;
        if (snd.Enabled && !(snd.DistractionsOnly && trigger == AlertTrigger.NoInput))
        {
            alert.Beeps = 1;
            ev.Add(new BeepRequested(trigger));
        }
        ev.Add(new AlertFired(Info(s, alert)));
    }

    private void ClearAlert(ActiveSession s, DateTimeOffset now, bool refocused, AlertResponse? forced, List<EngineEvent> ev)
    {
        if (s.Alert is not { } a) return;
        s.Alert = null;
        var response = forced ?? a.Response ?? AlertResponse.Ignored;
        _store.UpdateAlert(a.Id, Keys.Of(response), a.RespondedAt ?? (forced is null ? null : now), refocused ? now : null);
        ev.Add(new AlertCleared(a.Id, response));
    }

    private void BeginBreak(ActiveSession s, DateTimeOffset now, List<EngineEvent> ev)
    {
        CloseSegment(s, now);
        CloseGap(s, now);
        ClearAlert(s, now, refocused: false, AlertResponse.Break, ev);
        s.Gap = new OpenGap { Start = now, Label = "break" };
        s.State = SessionState.OnBreak;
        s.BreakUntil = now + TimeSpan.FromMinutes(Math.Max(0.1, _settings().Alerts.BreakMinutes));
        ResetRuns(s);
        UpdateSnapshot(now);
    }

    private void Suspend(ActiveSession s, DateTimeOffset now, SessionState state, string label, List<EngineEvent> ev)
    {
        CloseSegment(s, now);
        CloseGap(s, now);
        ClearAlert(s, now, refocused: false, null, ev);
        s.Gap = new OpenGap { Start = now, Label = label };
        s.State = state;
        s.PausedAt = now;
        s.BreakUntil = null;
        ResetRuns(s);
        UpdateSnapshot(now);
    }

    private void Unsuspend(ActiveSession s, DateTimeOffset now)
    {
        CloseGap(s, now);
        s.State = SessionState.Running;
        s.PausedAt = null;
        s.LastTick = now;
        ResetRuns(s);
        UpdateSnapshot(now);
    }

    private void End(DateTimeOffset at, EndReason reason, List<EngineEvent> ev)
    {
        var s = _s!;
        if (at < s.Start) at = s.Start;
        CloseSegment(s, at);
        CloseGap(s, at);
        ClearAlert(s, at, refocused: false, null, ev);
        if (s.Apps.Count > 0) _store.UpsertSessionApps(s.Id, AppRecords(s));
        _store.EndSession(s.Id, at, Keys.Of(reason));
        LastEndReason = reason;
        LastEndedAt = at;
        LastPlanName = s.Plan.Name;
        ev.Add(new SessionEnded(s.Id, reason, at, s.Plan.Name));
        _s = null;
        _snapshot = EngineSnapshot.Empty;
    }

    private void CloseSegment(ActiveSession s, DateTimeOffset at)
    {
        var seg = s.Segment;
        if (seg is null) return;
        s.Segment = null;
        if (at - seg.Start < TimeSpan.FromSeconds(1)) return;
        _store.AddSegment(ToRecord(s, seg, at));
        s.LastBoundary = at;
        if (seg.Match.List == ListKind.Focus) s.PrepClosedToday += LocalTime.OverlapDay(seg.Start, at, s.Day, _clock.Zone);
        GetApp(s, seg.Obs.Process, seg.Start).FocusSeconds += (at - seg.Start).TotalSeconds;
    }

    private void CloseGap(ActiveSession s, DateTimeOffset at)
    {
        var g = s.Gap;
        if (g is null) return;
        s.Gap = null;
        var label = g.Label ?? (s.ReadingGap ? "reading" : null);
        s.ReadingGap = false;
        if (at - g.Start < TimeSpan.FromSeconds(1)) return;
        _store.AddIdleGap(new IdleGapRecord(s.Id, g.Start, at, label, s.Plan.Slug));
        s.LastBoundary = at;
        if (label == "reading") s.PrepClosedToday += LocalTime.OverlapDay(g.Start, at, s.Day, _clock.Zone);
    }

    private SegmentRecord ToRecord(ActiveSession s, OpenSegment seg, DateTimeOffset end)
    {
        var m = seg.Match;
        var o = seg.Obs;
        // Privacy: window titles are kept only for focus-list matches.
        var title = m.List == ListKind.Focus ? Truncate(o.Title, 200) : null;
        return new SegmentRecord(s.Id, seg.Start, end, o.Process, o.IsPrivate ? "private" : o.Domain, title, Keys.Of(m.List),
            s.Plan.Slug, m.Topic?.Name, m.Topic is null ? null : Categories.Key(m.Topic.Category), m.Entry?.Raw);
    }

    private void UpdateSnapshot(DateTimeOffset now, TimeSpan? idleFor = null)
    {
        var s = _s;
        if (s is null) { _snapshot = EngineSnapshot.Empty; return; }

        var live = TimeSpan.Zero;
        if (s.Segment is { Match.List: ListKind.Focus } seg) live += LocalTime.OverlapDay(seg.Start, now, s.Day, _clock.Zone);
        if (s.Gap is { Label: null } g && s.ReadingGap) live += LocalTime.OverlapDay(g.Start, now, s.Day, _clock.Zone);

        var focus = s.State switch
        {
            SessionState.Paused or SessionState.Locked => FocusState.Paused,
            SessionState.OnBreak => FocusState.OnBreak,
            _ when s.Alert is not null => FocusState.FocusLost,
            _ when s.ReadingGap => FocusState.Focused,
            _ when s.Gap is not null => FocusState.Idle,
            _ when s.Current?.List == ListKind.Focus => FocusState.Focused,
            _ => FocusState.Drifting,
        };

        _snapshot = new EngineSnapshot(
            s.State, focus, s.Id, s.Plan.Slug, s.Plan.Name, s.Start, now - s.Start,
            s.CurrentObs?.Display, s.Current?.List, s.Current?.Topic?.Name,
            idleFor ?? _snapshot.IdleFor,
            s.PrepClosedToday + live, s.Plan.ExpectedOn(s.Day),
            s.Alert is { } a ? Info(s, a) : null, s.BreakUntil, s.LastFocusDisplay);
    }

    private ActiveAlertInfo Info(ActiveSession s, OpenAlert a) =>
        new(a.Id, a.Trigger, a.FiredAt, a.Since, a.What, a.LastFocus, s.Plan.Name, a.Response is not null);

    private TimeSpan PrepFromStore(Plan plan, DateOnly day) =>
        _store.SumPrep(plan.Slug, LocalTime.StartOfDay(day, _clock.Zone), LocalTime.EndOfDay(day, _clock.Zone));

    private static void ResetRuns(ActiveSession s)
    {
        s.DistractionSince = null;
        s.OffTaskSince = null;
    }

    private static string SegmentKey(Observation o, Classification m) =>
        $"{o.Process}|{o.Domain}|{o.IsPrivate}|{m.List}|{m.Entry?.Raw}|{m.Topic?.Name}";

    private static string FocusDisplay(Observation o)
    {
        var title = Truncate(o.Title, 60);
        return title is null ? o.Display : $"{o.Display} · {title}";
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private string AppListKey(string process, Plan plan)
    {
        if (Browsers.Is(process)) return "browser";
        var obs = new Observation(process, null, null, false, false);
        return Keys.Of(Classifier.Classify(obs, plan, _catalog().Distractions, LocalTime.DateOf(_clock.Now, _clock.Zone)).List);
    }

    private static AppUsage GetApp(ActiveSession s, string process, DateTimeOffset at)
    {
        if (!s.Apps.TryGetValue(process, out var app))
        {
            app = new AppUsage { FirstSeen = at, LastSeen = at };
            s.Apps[process] = app;
        }
        return app;
    }

    private static List<SessionAppRecord> AppRecords(ActiveSession s) =>
        s.Apps.Select(kv => new SessionAppRecord(s.Id, kv.Key, kv.Value.FirstSeen, kv.Value.LastSeen,
            Math.Round(kv.Value.OpenSeconds, 1), Math.Round(kv.Value.FocusSeconds, 1), kv.Value.List)).ToList();

    /// <summary>Runs a command under the lock, then raises events and the snapshot outside it.</summary>
    private T Run<T>(Func<List<EngineEvent>, T> body)
    {
        var ev = new List<EngineEvent>();
        T result;
        EngineSnapshot snap;
        lock (_gate)
        {
            result = body(ev);
            snap = _snapshot;
        }
        foreach (var e in ev) Event?.Invoke(e);
        Changed?.Invoke(snap);
        return result;
    }

    // ------------------------------------------------------------------ state

    private sealed class ActiveSession
    {
        public long Id;
        public Plan Plan = null!;
        public DateTimeOffset Start;
        public SessionState State = SessionState.Running;
        public OpenSegment? Segment;
        public OpenGap? Gap;
        public bool ReadingGap;
        public DateTimeOffset? LastTick;
        public DateTimeOffset? LastBoundary;
        public DateTimeOffset LastHeartbeat;
        public DateTimeOffset? PausedAt;
        public DateTimeOffset? BreakUntil;
        public DateTimeOffset? DistractionSince;
        public DateTimeOffset? OffTaskSince;
        public OpenAlert? Alert;
        public Suggestion? Suggestion;
        public DateOnly Day;
        public TimeSpan PrepClosedToday;
        public string? LastFocusDisplay;
        public Observation? CurrentObs;
        public Classification? Current;
        public readonly Dictionary<string, AppUsage> Apps = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? LastAppsSnapshot;
        public bool RemindedDistractions;
    }

    private sealed class OpenSegment
    {
        public DateTimeOffset Start;
        public Observation Obs = null!;
        public Classification Match = null!;
        public string Key = "";
    }

    private sealed class OpenGap
    {
        public DateTimeOffset Start;
        /// <summary>null = idle; otherwise break, paused or locked.</summary>
        public string? Label;
    }

    private sealed class OpenAlert
    {
        public long Id;
        public AlertTrigger Trigger;
        public DateTimeOffset FiredAt;
        public DateTimeOffset Since;
        public string? What;
        public string? LastFocus;
        public AlertResponse? Response;
        public DateTimeOffset? RespondedAt;
        public int Beeps;
        public DateTimeOffset LastBeep;
    }

    private sealed class AppUsage
    {
        public DateTimeOffset FirstSeen;
        public DateTimeOffset LastSeen;
        public double OpenSeconds;
        public double FocusSeconds;
        public string List = "neither";
    }

    private sealed record Suggestion(string Slug, DateTimeOffset Since, bool Sent);
}
