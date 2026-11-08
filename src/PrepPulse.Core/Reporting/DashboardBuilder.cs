using System.Globalization;
using PrepPulse.Core.Engine;
using PrepPulse.Core.Matching;
using PrepPulse.Core.Plans;

namespace PrepPulse.Core.Reporting;

/// <summary>
/// Pure function from (plans, stored records, now) to the dashboard model. No I/O, so it is unit-tested directly
/// and used both by the live dashboard and by the --report command.
/// </summary>
public static class DashboardBuilder
{
    public const int DaysShown = 14;
    public const int WeeksShown = 8;

    /// <summary>The earliest instant the dashboard needs: 8 weeks back, or the earliest plan start if older.</summary>
    public static DateTimeOffset RangeStart(PlanCatalogSnapshot catalog, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = LocalTime.DateOf(now, zone);
        var from = LocalTime.WeekStart(today).AddDays(-7 * (WeeksShown - 1));
        var days = today.AddDays(-(DaysShown - 1));
        if (days < from) from = days;
        foreach (var p in catalog.Plans)
            if (p.Start < from) from = p.Start;
        return LocalTime.StartOfDay(from, zone);
    }

    public static DashboardModel Build(PlanCatalogSnapshot catalog, ReportData data, string? selected, DateTimeOffset now, TimeZoneInfo zone, EngineSnapshot? live = null)
    {
        var b = new Builder(catalog, data, selected, now, zone);
        return b.Build(live);
    }

    private sealed class Builder
    {
        private readonly PlanCatalogSnapshot _catalog;
        private readonly DateTimeOffset _now;
        private readonly TimeZoneInfo _zone;
        private readonly DateOnly _today;
        private readonly Plan? _sel;
        private readonly IReadOnlyList<Plan> _scopePlans;
        private readonly List<SessionRecord> _sessions;
        private readonly List<SegmentRecord> _segments;
        private readonly List<IdleGapRecord> _gaps;
        private readonly List<AlertRecord> _alerts;
        private readonly List<SessionAppRecord> _apps;
        // Prep intervals (focus segments + pauses marked reading) per plan slug.
        private readonly Dictionary<string, List<(DateTimeOffset Start, DateTimeOffset End)>> _prep = new(StringComparer.OrdinalIgnoreCase);

        public Builder(PlanCatalogSnapshot catalog, ReportData data, string? selected, DateTimeOffset now, TimeZoneInfo zone)
        {
            _catalog = catalog;
            _now = now;
            _zone = zone;
            _today = LocalTime.DateOf(now, zone);
            _sel = catalog.Find(selected);
            _scopePlans = _sel is null ? catalog.Plans : new List<Plan> { _sel };

            _segments = data.Segments.Where(s => InScope(s.PlanSlug)).OrderBy(s => s.Start).ToList();
            _gaps = data.Gaps.Where(g => InScope(g.PlanSlug)).OrderBy(g => g.Start).ToList();
            var ids = _segments.Select(s => s.SessionId).Concat(_gaps.Select(g => g.SessionId)).ToHashSet();
            _sessions = data.Sessions.Where(s => InScope(s.PlanSlug) || ids.Contains(s.Id)).OrderBy(s => s.Start).ToList();
            var sessionIds = _sessions.Select(s => s.Id).ToHashSet();
            _alerts = data.Alerts.Where(a => sessionIds.Contains(a.SessionId)).OrderBy(a => a.FiredAt).ToList();
            _apps = data.Apps.Where(a => sessionIds.Contains(a.SessionId)).ToList();

            foreach (var s in data.Segments.Where(s => s.List == "focus" && s.PlanSlug is not null))
                Intervals(s.PlanSlug!).Add((s.Start, s.End));
            foreach (var g in data.Gaps.Where(g => g.Label == "reading" && g.PlanSlug is not null))
                Intervals(g.PlanSlug!).Add((g.Start, g.End));
        }

        private bool InScope(string? slug) => _sel is null || string.Equals(slug, _sel.Slug, StringComparison.OrdinalIgnoreCase);

        private List<(DateTimeOffset Start, DateTimeOffset End)> Intervals(string slug)
        {
            if (!_prep.TryGetValue(slug, out var list)) _prep[slug] = list = [];
            return list;
        }

        private TimeSpan PrepOn(DateOnly day, string? onlySlug = null)
        {
            var total = TimeSpan.Zero;
            foreach (var (slug, list) in _prep)
            {
                if (onlySlug is not null ? !string.Equals(slug, onlySlug, StringComparison.OrdinalIgnoreCase) : !InScope(slug)) continue;
                foreach (var (s, e) in list) total += LocalTime.OverlapDay(s, e, day, _zone);
            }
            return total;
        }

        private TimeSpan PrepBetween(DateOnly from, DateOnly to, string? onlySlug = null)
        {
            var total = TimeSpan.Zero;
            for (var d = from; d <= to; d = d.AddDays(1)) total += PrepOn(d, onlySlug);
            return total;
        }

        private TimeSpan ExpectedOn(DateOnly day) => _scopePlans.Aggregate(TimeSpan.Zero, (t, p) => t + p.ExpectedOn(day));

        private DateTimeOffset EndOf(SessionRecord s) => s.End ?? _now;

        public DashboardModel Build(EngineSnapshot? live)
        {
            var todaySessions = _sessions.Where(s => LocalTime.OverlapDay(s.Start, EndOf(s), _today, _zone) > TimeSpan.Zero).ToList();

            return new DashboardModel(
                GeneratedAt: _now.ToString("o", CultureInfo.InvariantCulture),
                Today: Iso(_today),
                Selected: _sel?.Slug,
                Plans: _catalog.Plans.Select(PlanSummaryFor).ToList(),
                Tiles: Tiles(todaySessions),
                Timeline: Timeline(todaySessions),
                TimeUse: TimeUseToday(),
                SessionsToday: SessionRows(todaySessions),
                Days: Days(),
                BurnUp: BurnUp(),
                Weeks: Weeks(),
                FocusRatio: FocusRatios(),
                Apps: OpenApps(todaySessions),
                Alerts: AlertsToday(),
                Topics: Topics(),
                Live: live is { IsRunning: true } l
                    ? new LiveModel(l.Session.ToString(), l.Focus.ToString(), l.PlanName, l.Current, Min(l.SessionLength), Min(l.PrepToday), Min(l.TargetToday))
                    : null,
                PlanErrors: _catalog.Errors.Select(e => $"{e.File}: {e.Message}").ToList());
        }

        // ---------------------------------------------------------------- plan cards

        private PlanSummary PlanSummaryFor(Plan p)
        {
            var doneAll = PrepBetween(Max(p.Start, MinDataDay()), _today, p.Slug);
            var week0 = LocalTime.WeekStart(_today);
            var weekDone = PrepBetween(week0, _today, p.Slug);
            var weekTarget = p.ExpectedBetween(week0, week0.AddDays(6));
            var (pace, behind) = Pace(p, doneAll);
            double? planHours = p.TotalTopicHours ?? (p.End is { } e ? (double?)p.ExpectedBetween(p.Start, e).TotalHours : null);
            return new PlanSummary(
                p.Slug, p.Name, p.ColourHex, Iso(p.Start), p.End is { } end ? Iso(end) : null,
                p.End is { } e2 ? (int?)Math.Max(0, e2.DayNumber - _today.DayNumber) : null,
                p.TargetSummary, Round(doneAll.TotalHours, 2), planHours is null ? null : (double?)Round(planHours.Value, 1),
                Min(PrepOn(_today, p.Slug)), Min(p.ExpectedOn(_today)), Min(weekDone), Min(weekTarget),
                pace, behind, p.Paused, p.Topics.Count);
        }

        private (double? Pace, double? Behind) Pace(Plan p, TimeSpan doneToDate)
        {
            if (_today < p.Start) return (null, null);
            var last = p.End is { } e && e < _today ? e : _today;
            var expected = p.ExpectedBetween(p.Start, last);
            if (expected <= TimeSpan.Zero) return (null, null);
            return (Round(doneToDate / expected * 100, 0), Round((expected - doneToDate).TotalHours, 1));
        }

        private DateOnly MinDataDay()
        {
            var first = _sessions.Count > 0 ? _sessions.Min(s => s.Start) : _now;
            return LocalTime.DateOf(first, _zone);
        }

        // ---------------------------------------------------------------- today

        private TodayTiles Tiles(List<SessionRecord> todaySessions)
        {
            var prep = PrepOn(_today);
            var sessionLen = todaySessions.Aggregate(TimeSpan.Zero, (t, s) => t + LocalTime.OverlapDay(s.Start, EndOf(s), _today, _zone));

            // Longest run of focus segments with no gap or other segment between them.
            var longest = TimeSpan.Zero;
            var run = TimeSpan.Zero;
            SegmentRecord? prev = null;
            foreach (var s in _segments.Where(s => LocalTime.OverlapDay(s.Start, s.End, _today, _zone) > TimeSpan.Zero))
            {
                if (s.List != "focus") { run = TimeSpan.Zero; prev = null; continue; }
                var part = LocalTime.OverlapDay(s.Start, s.End, _today, _zone);
                var contiguous = prev is not null && prev.SessionId == s.SessionId && (s.Start - prev.End).Duration() <= TimeSpan.FromSeconds(5);
                run = contiguous ? run + part : part;
                if (run > longest) longest = run;
                prev = s;
            }

            var idle = _gaps.Where(g => (g.Label is null || g.Label == "reading") && LocalTime.OverlapDay(g.Start, g.End, _today, _zone) > TimeSpan.Zero).ToList();
            var idleTime = idle.Aggregate(TimeSpan.Zero, (t, g) => t + LocalTime.OverlapDay(g.Start, g.End, _today, _zone));

            double? pace = null, behind = null;
            var expected = TimeSpan.Zero;
            var actual = TimeSpan.Zero;
            foreach (var p in _scopePlans)
            {
                if (_today < p.Start) continue;
                var last = p.End is { } e && e < _today ? e : _today;
                expected += p.ExpectedBetween(p.Start, last);
                actual += PrepBetween(Max(p.Start, MinDataDay()), last, p.Slug);
            }
            if (expected > TimeSpan.Zero)
            {
                pace = Round(actual / expected * 100, 0);
                behind = Round((expected - actual).TotalHours, 1);
            }

            var streak = 0;
            var day = PrepOn(_today) >= TimeSpan.FromMinutes(1) ? _today : _today.AddDays(-1);
            var floor = MinDataDay();
            while (day >= floor && PrepOn(day) >= TimeSpan.FromMinutes(1)) { streak++; day = day.AddDays(-1); }

            return new TodayTiles(Min(prep), Min(ExpectedOn(_today)), todaySessions.Count, Min(sessionLen),
                sessionLen > TimeSpan.Zero ? (double?)Round(prep / sessionLen, 3) : null,
                Min(longest), idle.Count, Min(idleTime), pace, behind, streak);
        }

        private List<TimelineSession> Timeline(List<SessionRecord> todaySessions)
        {
            var result = new List<TimelineSession>();
            var n = 0;
            foreach (var s in todaySessions)
            {
                n++;
                var blocks = new List<TimelineBlock>();
                foreach (var seg in _segments.Where(x => x.SessionId == s.Id))
                {
                    if (LocalTime.OverlapDay(seg.Start, seg.End, _today, _zone) <= TimeSpan.Zero) continue;
                    var kind = seg.List switch { "focus" => seg.Category ?? "other", "distraction" => "distraction", _ => "neither" };
                    blocks.Add(new TimelineBlock(Mins(seg.Start), Mins(seg.End), kind, Label(seg), seg.Title, seg.Topic));
                }
                foreach (var g in _gaps.Where(x => x.SessionId == s.Id))
                {
                    if (LocalTime.OverlapDay(g.Start, g.End, _today, _zone) <= TimeSpan.Zero) continue;
                    var kind = g.Label ?? "idle";
                    blocks.Add(new TimelineBlock(Mins(g.Start), Mins(g.End), kind, GapLabel(kind), null, null));
                }
                blocks.Sort((a, c) => a.Start.CompareTo(c.Start));

                var alerts = _alerts.Where(a => a.SessionId == s.Id && LocalTime.DateOf(a.FiredAt, _zone) == _today)
                    .Select(a => new TimelineAlert(Mins(a.FiredAt), Keys.TriggerLabel(a.Trigger), What(a.Domain, a.Process), Keys.ResponseLabel(a.Response)))
                    .ToList();

                result.Add(new TimelineSession(n, s.PlanName, Mins(s.Start), Mins(EndOf(s)), Keys.EndReasonLabel(s.EndReason), s.End is null, blocks, alerts));
            }
            return result;
        }

        private List<TimeUse> TimeUseToday()
        {
            return _segments
                .Where(s => LocalTime.OverlapDay(s.Start, s.End, _today, _zone) > TimeSpan.Zero)
                .GroupBy(Label)
                .Select(g =>
                {
                    var minutes = g.Aggregate(TimeSpan.Zero, (t, s) => t + LocalTime.OverlapDay(s.Start, s.End, _today, _zone));
                    var main = g.GroupBy(s => s.List == "focus" ? s.Category ?? "other" : s.List)
                        .OrderByDescending(k => k.Sum(s => (s.End - s.Start).TotalSeconds)).First().Key;
                    return new TimeUse(g.Key, Min(minutes), main);
                })
                .Where(t => t.Minutes >= 0.5)
                .OrderByDescending(t => t.Minutes)
                .Take(15)
                .ToList();
        }

        private List<SessionRow> SessionRows(List<SessionRecord> todaySessions)
        {
            var rows = new List<SessionRow>();
            var n = 0;
            foreach (var s in todaySessions)
            {
                n++;
                var end = EndOf(s);
                var len = end - s.Start;
                var segs = _segments.Where(x => x.SessionId == s.Id).ToList();
                var gaps = _gaps.Where(x => x.SessionId == s.Id).ToList();
                var prep = segs.Where(x => x.List == "focus").Aggregate(TimeSpan.Zero, (t, x) => t + (x.End - x.Start))
                         + gaps.Where(x => x.Label == "reading").Aggregate(TimeSpan.Zero, (t, x) => t + (x.End - x.Start));
                var idle = gaps.Where(x => x.Label is null || x.Label == "reading").ToList();
                var distraction = segs.Where(x => x.List == "distraction").Aggregate(TimeSpan.Zero, (t, x) => t + (x.End - x.Start));
                rows.Add(new SessionRow(n, s.PlanName, Clock(s.Start), s.End is null ? "running" : Clock(end), Min(len), Min(prep),
                    Min(idle.Aggregate(TimeSpan.Zero, (t, x) => t + (x.End - x.Start))), idle.Count, Min(distraction),
                    len > TimeSpan.Zero ? (double?)Round(prep / len * 100, 0) : null, Keys.EndReasonLabel(s.EndReason)));
            }
            return rows;
        }

        // ---------------------------------------------------------------- history

        private List<DayTotal> Days()
        {
            var days = new List<DayTotal>();
            for (var i = DaysShown - 1; i >= 0; i--)
            {
                var d = _today.AddDays(-i);
                var by = new Dictionary<string, double> { ["coding"] = 0, ["design"] = 0, ["ai"] = 0, ["mock"] = 0, ["other"] = 0 };
                foreach (var s in _segments.Where(x => x.List == "focus"))
                {
                    var part = LocalTime.OverlapDay(s.Start, s.End, d, _zone);
                    if (part <= TimeSpan.Zero) continue;
                    var key = s.Category is { } c && by.ContainsKey(c) ? c : "other";
                    by[key] += part.TotalMinutes;
                }
                foreach (var g in _gaps.Where(x => x.Label == "reading"))
                    by["other"] += LocalTime.OverlapDay(g.Start, g.End, d, _zone).TotalMinutes;
                foreach (var k in by.Keys.ToList()) by[k] = Round(by[k], 1);
                days.Add(new DayTotal(Iso(d), by, Round(by.Values.Sum(), 1), Min(ExpectedOn(d))));
            }
            return days;
        }

        private BurnUpModel? BurnUp()
        {
            if (_sel is null || _sel.End is not { } end) return null;
            var p = _sel;
            var count = end.DayNumber - p.Start.DayNumber + 1;
            if (count <= 0 || count > 3660) return null;
            var todayIndex = _today < p.Start ? -1 : Math.Min(count - 1, _today.DayNumber - p.Start.DayNumber);

            var planned = new List<double>(count);
            var actual = new List<double?>(count);
            var projected = new List<double?>(count);
            double cumPlan = 0, cumActual = 0;
            for (var i = 0; i < count; i++)
            {
                var d = p.Start.AddDays(i);
                cumPlan += p.ExpectedOn(d).TotalHours;
                planned.Add(Round(cumPlan, 2));
                if (i <= todayIndex)
                {
                    cumActual += PrepOn(d, p.Slug).TotalHours;
                    actual.Add(Round(cumActual, 2));
                }
                else actual.Add(null);
            }

            var pacePerDay = PrepBetween(_today.AddDays(-6), _today, p.Slug).TotalHours / 7;
            var atToday = todayIndex >= 0 ? actual[todayIndex] ?? 0 : 0;
            for (var i = 0; i < count; i++)
                projected.Add(todayIndex >= 0 && i >= todayIndex ? (double?)Round(atToday + pacePerDay * (i - todayIndex), 2) : null);

            return new BurnUpModel(p.Name, Iso(p.Start), Iso(end), todayIndex, planned, actual, projected);
        }

        private List<WeekTotal> Weeks()
        {
            var weeks = new List<WeekTotal>();
            var thisWeek = LocalTime.WeekStart(_today);
            for (var i = WeeksShown - 1; i >= 0; i--)
            {
                var ws = thisWeek.AddDays(-7 * i);
                var lastDay = ws.AddDays(6) < _today ? ws.AddDays(6) : _today;
                var target = TimeSpan.Zero;
                for (var d = ws; d <= ws.AddDays(6); d = d.AddDays(1)) target += ExpectedOn(d);
                weeks.Add(new WeekTotal(Iso(ws), Min(PrepBetween(ws, lastDay)), Min(target)));
            }
            return weeks;
        }

        private List<FocusRatioDay> FocusRatios()
        {
            var list = new List<FocusRatioDay>();
            for (var i = DaysShown - 1; i >= 0; i--)
            {
                var d = _today.AddDays(-i);
                var len = _sessions.Aggregate(TimeSpan.Zero, (t, s) => t + LocalTime.OverlapDay(s.Start, EndOf(s), d, _zone));
                list.Add(new FocusRatioDay(Iso(d), len >= TimeSpan.FromMinutes(1) ? (double?)Round(PrepOn(d) / len, 3) : null));
            }
            return list;
        }

        private OpenAppsModel? OpenApps(List<SessionRecord> todaySessions)
        {
            var s = _sessions.LastOrDefault();
            if (s is null) return null;
            var n = todaySessions.FindIndex(x => x.Id == s.Id) + 1;
            var rows = new List<OpenAppRow>();
            var segs = _segments.Where(x => x.SessionId == s.Id).ToList();

            foreach (var app in _apps.Where(a => a.SessionId == s.Id).OrderByDescending(a => a.OpenSeconds).ThenByDescending(a => a.FocusSeconds))
            {
                var name = Observation.ProcessDisplay(app.Process);
                if (app.List == "browser")
                {
                    var sites = segs.Where(x => string.Equals(x.Process, app.Process, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(x => x.Domain is null && x.Entry is null ? "(other pages)" : Label(x))
                        .Select(g => (Name: g.Key, List: g.First().List, Minutes: g.Sum(x => (x.End - x.Start).TotalMinutes)))
                        .OrderByDescending(x => x.Minutes).ToList();
                    var focusSites = sites.Count(x => x.List == "focus");
                    var distractSites = sites.Count(x => x.List == "distraction");
                    rows.Add(new OpenAppRow(name, "mixed", Round(app.OpenSeconds / 60, 1), Round(app.FocusSeconds / 60, 1), false,
                        $"{focusSites} focus site{(focusSites == 1 ? "" : "s")}, {distractSites} distraction{(distractSites == 1 ? "" : "s")}"));
                    foreach (var site in sites)
                        rows.Add(new OpenAppRow(site.Name, site.List, 0, Round(site.Minutes, 1), true, null));
                }
                else
                {
                    string? note = null;
                    if (app.List == "distraction" && app.OpenSeconds >= 60 && app.FocusSeconds < app.OpenSeconds * 0.1)
                        note = app.FocusSeconds < 1 ? "open in the background, never focused" : "open in the background";
                    rows.Add(new OpenAppRow(name, app.List, Round(app.OpenSeconds / 60, 1), Round(app.FocusSeconds / 60, 1), false, note));
                }
            }

            return new OpenAppsModel(n, Iso(LocalTime.DateOf(s.Start, _zone)), Clock(s.Start), s.End is null ? "running" : Clock(s.End.Value),
                Keys.EndReasonLabel(s.EndReason), rows);
        }

        private List<AlertRow> AlertsToday() =>
            _alerts.Where(a => LocalTime.DateOf(a.FiredAt, _zone) == _today)
                .Select(a => new AlertRow(Clock(a.FiredAt), Keys.TriggerLabel(a.Trigger), What(a.Domain, a.Process), Keys.ResponseLabel(a.Response),
                    a.RefocusedAt is { } r ? (double?)Round((r - a.FiredAt).TotalMinutes, 1) : null))
                .ToList();

        private List<TopicProgress> Topics()
        {
            var list = new List<TopicProgress>();
            foreach (var p in _scopePlans)
            {
                foreach (var t in p.Topics)
                {
                    var done = _segments.Where(s => s.List == "focus" && string.Equals(s.PlanSlug, p.Slug, StringComparison.OrdinalIgnoreCase) && s.Topic == t.Name)
                        .Sum(s => (s.End - s.Start).TotalHours);
                    double? due = null;
                    if (t.Hours is { } hours)
                    {
                        var wStart = t.WeekFrom is { } wf ? p.Start.AddDays(7 * (wf - 1)) : p.Start;
                        DateOnly? wEnd = t.WeekTo is { } wt ? p.Start.AddDays(7 * wt - 1) : p.End;
                        if (p.End is { } pe && wEnd is { } we && we > pe) wEnd = pe;
                        if (wEnd is { } windowEnd && windowEnd >= wStart)
                        {
                            var total = windowEnd.DayNumber - wStart.DayNumber + 1;
                            var elapsed = Math.Clamp(_today.DayNumber - wStart.DayNumber + 1, 0, total);
                            due = Round(hours * elapsed / total, 1);
                        }
                    }
                    list.Add(new TopicProgress(p.Name, t.Name, Categories.Key(t.Category), Round(done, 2), due, t.Hours));
                }
            }
            return list;
        }

        // ---------------------------------------------------------------- helpers

        private double Mins(DateTimeOffset t) => Round(Math.Clamp(LocalTime.MinutesIntoDay(t, _today, _zone), 0, 1440), 2);

        private string Clock(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, _zone).ToString("HH:mm", CultureInfo.InvariantCulture);

        /// <summary>Domain if known; for a browser matched by its window title, the site from the matching entry; else the program.</summary>
        private static string Label(SegmentRecord s)
        {
            if (s.Domain == "private") return "Private browsing";
            if (s.Domain is { } d) return d;
            if (Browsers.Is(s.Process) && s.Entry is { } e && !e.Contains(".exe", StringComparison.OrdinalIgnoreCase))
                return e.Split('|')[0].Trim();
            return Observation.ProcessDisplay(s.Process);
        }

        private static string? What(string? domain, string? process) =>
            domain == "private" ? "Private browsing" : domain ?? (process is null ? null : Observation.ProcessDisplay(process));

        private static string GapLabel(string kind) => kind switch
        {
            "idle" => "No input",
            "reading" => "Reading / thinking",
            "break" => "Break",
            "paused" => "Paused",
            "locked" => "PC locked",
            _ => kind,
        };

        private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
        private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private static double Min(TimeSpan t) => Round(t.TotalMinutes, 1);
        private static double Round(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
    }
}
