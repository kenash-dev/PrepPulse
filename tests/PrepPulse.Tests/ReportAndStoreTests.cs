using Microsoft.Data.Sqlite;
using PrepPulse.Core;
using PrepPulse.Core.Reporting;
using PrepPulse.Data;
using static PrepPulse.Tests.TestPlans;

namespace PrepPulse.Tests;

public class ReportTests
{
    private static (EngineHarness H, ReportData Data) MorningSession()
    {
        var h = new EngineHarness(FakeClock.At(2026, 11, 9, 9, 0));
        h.Engine.StartSession("interview-prep");
        h.SpendMinutes(30, Site("leetcode.com", "3Sum - LeetCode"));
        h.SpendMinutes(3, App("whatsapp.exe", "WhatsApp"));
        h.SpendMinutes(10, Site("bytebytego.com", "Rate limiter"));
        h.Engine.StopSession();
        var data = new ReportData(h.Store.Sessions, h.Store.Segments, h.Store.Gaps, h.Store.Alerts, h.Store.Apps.Values.ToList());
        return (h, data);
    }

    [Fact]
    public void Today_tiles_timeline_and_tables_come_from_the_session()
    {
        var (h, data) = MorningSession();
        var m = DashboardBuilder.Build(h.Catalog, data, "interview-prep", h.Clock.Now, FakeClock.Sydney);

        Assert.Equal("interview-prep", m.Selected);
        Assert.InRange(m.Tiles.PrepMinutes, 39.8, 40.1);
        Assert.Equal(120, m.Tiles.TargetMinutes);
        Assert.Equal(1, m.Tiles.Sessions);
        Assert.InRange(m.Tiles.FocusRatio!.Value, 0.9, 0.95);
        Assert.InRange(m.Tiles.LongestBlockMinutes, 29.8, 30.1);

        var session = Assert.Single(m.Timeline);
        Assert.Equal(new[] { "coding", "distraction", "design" }, session.Blocks.Select(b => b.Kind).ToArray());
        Assert.Equal(540, session.Start, 1);
        var alert = Assert.Single(session.Alerts);
        Assert.Equal("Distraction", alert.Trigger);

        Assert.Equal("leetcode.com", m.TimeUse[0].Label);
        var row = Assert.Single(m.SessionsToday);
        Assert.Equal("09:00", row.Start);
        Assert.Equal("Stop", row.EndedBy);
        Assert.InRange(row.DistractionMinutes, 2.9, 3.1);

        var a = Assert.Single(m.Alerts);
        Assert.Equal("whatsapp", a.What);
        Assert.Equal("No answer", a.Response);
        Assert.InRange(a.BackAfterMinutes!.Value, 0.9, 1.1);
    }

    [Fact]
    public void Daily_totals_burn_up_topics_and_plan_cards()
    {
        var (h, data) = MorningSession();
        var m = DashboardBuilder.Build(h.Catalog, data, "interview-prep", h.Clock.Now, FakeClock.Sydney);

        Assert.Equal(DashboardBuilder.DaysShown, m.Days.Count);
        var today = m.Days[^1];
        Assert.Equal("2026-11-09", today.Date);
        Assert.InRange(today.ByCategory["coding"], 29.8, 30.1);
        Assert.InRange(today.ByCategory["design"], 9.9, 10.1);
        Assert.Equal(120, today.Target);

        var bu = m.BurnUp!;
        Assert.Equal("2026-11-02", bu.Start);
        Assert.Equal(7, bu.TodayIndex);
        Assert.Equal(2, bu.Planned[0]);              // Monday 2 Nov: 2h
        Assert.Equal(2 * 5 + 4 * 2 + 2, bu.Planned[7]); // first full week + Monday
        Assert.InRange(bu.Actual[7]!.Value, 0.66, 0.67);
        Assert.Null(bu.Actual[8]);
        Assert.NotNull(bu.Projected[^1]);

        Assert.InRange(m.Topics.Single(t => t.Name == "Graphs").DoneHours, 0.49, 0.51);
        Assert.Equal(2, m.Plans.Count);
        Assert.Contains(m.Plans, p => p.Slug == "learning-ai" && p.End is null);
    }

    [Fact]
    public void All_plans_adds_targets_and_open_ended_plans_get_weeks_not_a_burn_up()
    {
        var (h, data) = MorningSession();
        var all = DashboardBuilder.Build(h.Catalog, data, null, h.Clock.Now, FakeClock.Sydney);
        Assert.Null(all.Selected);
        Assert.Equal(180, all.Tiles.TargetMinutes); // 2h interview + 7h/7 learning
        Assert.Null(all.BurnUp);
        Assert.Equal(DashboardBuilder.WeeksShown, all.Weeks.Count);

        var learning = DashboardBuilder.Build(h.Catalog, data, "learning-ai", h.Clock.Now, FakeClock.Sydney);
        Assert.Equal(0, learning.Tiles.PrepMinutes);
        Assert.Empty(learning.Timeline);
    }

    [Fact]
    public void Browser_matched_by_title_is_labelled_with_the_site()
    {
        // Found in the live test: an Edge app window (no address bar) matched reddit.com by title but showed as "msedge".
        var start = FakeClock.At(2026, 11, 9, 9, 0);
        var data = new ReportData(
            [new SessionRecord(1, "interview-prep", "Interview prep", start, start.AddMinutes(5), "manual", null)],
            [new SegmentRecord(1, start, start.AddMinutes(5), "msedge.exe", null, null, "distraction", "interview-prep", null, null, "reddit.com")],
            [], [], [new SessionAppRecord(1, "msedge.exe", start, start.AddMinutes(5), 300, 300, "browser")]);
        var m = DashboardBuilder.Build(Catalog(), data, "interview-prep", start.AddMinutes(6), FakeClock.Sydney);
        Assert.Equal("reddit.com", m.TimeUse.Single().Label);
        Assert.Contains(m.Apps!.Rows, r => r.Child && r.Name == "reddit.com" && r.List == "distraction");
    }

    [Fact]
    public void A_segment_across_midnight_is_split_between_days()
    {
        var start = FakeClock.At(2026, 11, 9, 23, 30);
        var end = FakeClock.At(2026, 11, 10, 0, 30);
        var data = new ReportData(
            [new SessionRecord(1, "interview-prep", "Interview prep", start, end, "manual", end)],
            [new SegmentRecord(1, start, end, "chrome.exe", "leetcode.com", null, "focus", "interview-prep", "Graphs", "coding", "leetcode.com")],
            [], [], []);
        var m = DashboardBuilder.Build(Catalog(), data, "interview-prep", FakeClock.At(2026, 11, 10, 1, 0), FakeClock.Sydney);
        Assert.Equal(30, m.Days[^2].Total, 1);
        Assert.Equal(30, m.Days[^1].Total, 1);
        Assert.Equal(30, m.Tiles.PrepMinutes, 1);
    }
}

public class StoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"preppulse-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Everything_the_engine_writes_reads_back()
    {
        var store = new SqliteStore(_path, FakeClock.Sydney);
        var t0 = FakeClock.At(2026, 11, 9, 9, 0);
        var id = store.StartSession("interview-prep", "Interview prep", t0);
        store.AddSegment(new SegmentRecord(id, t0, t0.AddMinutes(30), "chrome.exe", "leetcode.com", "3Sum", "focus", "interview-prep", "Graphs", "coding", "leetcode.com"));
        store.AddSegment(new SegmentRecord(id, t0.AddMinutes(30), t0.AddMinutes(35), "whatsapp.exe", null, null, "distraction", "interview-prep", null, null, "WhatsApp.exe"));
        store.AddIdleGap(new IdleGapRecord(id, t0.AddMinutes(35), t0.AddMinutes(45), "reading", "interview-prep"));
        var alertId = store.AddAlert(new AlertRecord(0, id, t0.AddMinutes(32), "distraction", t0.AddMinutes(30), "whatsapp.exe", null, null, null, null));
        store.UpdateAlert(alertId, "back", t0.AddMinutes(33), null);
        store.UpdateAlert(alertId, null, null, t0.AddMinutes(35));
        store.UpsertSessionApps(id, [new SessionAppRecord(id, "whatsapp.exe", t0, t0.AddMinutes(40), 2400, 300, "distraction")]);
        store.UpsertSessionApps(id, [new SessionAppRecord(id, "whatsapp.exe", t0, t0.AddMinutes(45), 2700, 300, "distraction")]);
        store.Heartbeat(id, t0.AddMinutes(45));

        Assert.Equal(TimeSpan.FromMinutes(40), store.SumPrep("interview-prep", t0.AddHours(-9), t0.AddHours(15)));
        Assert.Equal(TimeSpan.FromMinutes(25), store.SumPrep("interview-prep", t0.AddMinutes(10), t0.AddMinutes(40))); // 20 focus + 5 reading, clipped
        Assert.Equal("interview-prep", store.LastPlanSlug());

        store.EndSession(id, t0.AddMinutes(50), "manual");
        var data = store.Load(t0.AddHours(-1), t0.AddHours(2));

        var s = Assert.Single(data.Sessions);
        Assert.Equal(t0.AddMinutes(50), s.End);
        Assert.Equal("manual", s.EndReason);
        Assert.Equal(2, data.Segments.Count);
        Assert.Equal("Graphs", data.Segments[0].Topic);
        Assert.Equal(t0, data.Segments[0].Start);
        Assert.Equal("reading", Assert.Single(data.Gaps).Label);
        var alert = Assert.Single(data.Alerts);
        Assert.Equal("back", alert.Response);
        Assert.Equal(t0.AddMinutes(33), alert.RespondedAt);
        Assert.Equal(t0.AddMinutes(35), alert.RefocusedAt);
        Assert.Equal(2700, Assert.Single(data.Apps).OpenSeconds);
    }

    [Fact]
    public void Sessions_left_open_by_a_crash_are_closed_at_their_last_heartbeat()
    {
        var t0 = FakeClock.At(2026, 11, 9, 9, 0);
        var store = new SqliteStore(_path, FakeClock.Sydney);
        var id = store.StartSession("interview-prep", "Interview prep", t0);
        store.Heartbeat(id, t0.AddMinutes(7));

        var reopened = new SqliteStore(_path, FakeClock.Sydney); // migration runs again and must be harmless
        Assert.Equal(1, reopened.CloseUnfinishedSessions());
        Assert.Equal(0, reopened.CloseUnfinishedSessions());
        var s = Assert.Single(reopened.Load(t0.AddHours(-1), t0.AddHours(1)).Sessions);
        Assert.Equal("unexpected", s.EndReason);
        Assert.Equal(t0.AddMinutes(7), s.End);
    }

    [Fact]
    public void Engine_and_report_work_end_to_end_on_sqlite()
    {
        var h = new EngineHarness(FakeClock.At(2026, 11, 9, 9, 0));
        var store = new SqliteStore(_path, FakeClock.Sydney);
        var engine = new PrepPulse.Core.Engine.FocusEngine(h.Clock, store, () => h.Settings, () => h.Catalog);
        engine.StartSession("interview-prep");
        var input = h.Clock.Now;
        for (var i = 0; i < 600; i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            var what = i < 300 ? Site("leetcode.com", "3Sum") : App("whatsapp.exe");
            input = h.Clock.Now;
            engine.Tick(new PrepPulse.Core.Engine.Sample(h.Clock.Now, what, h.Clock.Now - input));
        }
        engine.StopSession();

        var m = DashboardBuilder.Build(h.Catalog, store.Load(h.Clock.Now.AddDays(-30), h.Clock.Now), "interview-prep", h.Clock.Now, FakeClock.Sydney);
        Assert.InRange(m.Tiles.PrepMinutes, 4.9, 5.1);
        Assert.Single(m.Alerts);
        Assert.Equal("Distraction", m.Alerts[0].Trigger);
    }
}
