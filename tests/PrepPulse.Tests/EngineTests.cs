using PrepPulse.Core.Engine;
using static PrepPulse.Tests.TestPlans;

namespace PrepPulse.Tests;

/// <summary>Drives the session engine second by second with a fake clock and checks what it records and raises.</summary>
public class EngineTests
{
    private static readonly PrepPulse.Core.Matching.Observation LeetCode = Site("leetcode.com", "3Sum - LeetCode");
    private static readonly PrepPulse.Core.Matching.Observation WhatsApp = App("whatsapp.exe", "WhatsApp");

    private static EngineHarness Started()
    {
        var h = new EngineHarness();
        Assert.True(h.Engine.StartSession("interview-prep"));
        return h;
    }

    [Fact]
    public void Records_focus_segments_with_plan_topic_and_title()
    {
        var h = Started();
        h.Spend(60, LeetCode);
        h.Spend(30, App("code.exe", "secret.txt - Code"));
        h.Engine.StopSession();

        Assert.Equal(2, h.Store.Segments.Count);
        var focus = h.Store.Segments[0];
        Assert.Equal("focus", focus.List);
        Assert.Equal("leetcode.com", focus.Domain);
        Assert.Equal("Graphs", focus.Topic);
        Assert.Equal("coding", focus.Category);
        Assert.Equal("3Sum - LeetCode", focus.Title);
        Assert.Equal(TimeSpan.FromSeconds(60), focus.End - focus.Start);

        var other = h.Store.Segments[1];
        Assert.Equal("neither", other.List);
        Assert.Null(other.Title); // titles are only kept for focus-list matches
        Assert.Equal("manual", h.Store.Sessions[0].EndReason);
    }

    [Fact]
    public void Snapshot_tracks_progress_against_todays_target()
    {
        var h = Started();
        h.SpendMinutes(30, LeetCode);
        var s = h.Engine.Snapshot;
        Assert.Equal(FocusState.Focused, s.Focus);
        Assert.Equal(TimeSpan.FromHours(2), s.TargetToday);
        Assert.InRange(s.PrepToday.TotalMinutes, 29.9, 30.1);
        Assert.InRange(s.TodayProgress, 0.24, 0.26);
    }

    [Fact]
    public void Idle_gap_is_backdated_to_the_last_input()
    {
        var h = Started();
        var start = h.Clock.Now;
        h.Spend(30, LeetCode);                    // last input at +30s
        h.Spend(150, LeetCode, typing: false);    // gap opens once 2 min have passed
        Assert.Equal(FocusState.Idle, h.Engine.Snapshot.Focus);
        h.Spend(10, LeetCode);                    // input again at +181s
        h.Engine.StopSession();

        var gap = Assert.Single(h.Store.Gaps);
        Assert.Null(gap.Label);
        Assert.Equal(start.AddSeconds(30), gap.Start);
        Assert.Equal(start.AddSeconds(181), gap.End);
        Assert.Equal(start.AddSeconds(30), h.Store.Segments[0].End);
        Assert.Equal(start.AddSeconds(181), h.Store.Segments[1].Start);
    }

    [Fact]
    public void Idle_pause_never_overlaps_the_previous_segment()
    {
        // Found in the live test: type in one app, switch to another without typing, then go idle.
        var h = Started();
        h.Spend(30, LeetCode);                 // last input at +30
        h.Spend(20, WhatsApp, typing: false);  // switched at +31 without input
        h.Spend(150, WhatsApp, typing: false); // idle
        h.Spend(5, LeetCode);
        h.Engine.StopSession();

        var gap = Assert.Single(h.Store.Gaps);
        var before = h.Store.Segments.Where(s => s.Start < gap.Start).ToList();
        Assert.All(before, s => Assert.True(s.End <= gap.Start));
        Assert.Equal(h.Store.Segments.Single(s => s.List == "focus" && s.Start < gap.Start).End, gap.Start);
    }

    [Fact]
    public void Passive_entries_dont_go_idle()
    {
        var h = Started();
        var talk = Site("youtube.com", "System Design Interview - YouTube");
        h.Spend(10, talk);
        h.Spend(400, talk, typing: false);
        h.Engine.StopSession();

        Assert.Empty(h.Store.Gaps);
        Assert.Empty(h.Store.Alerts);
        Assert.Equal(409, h.Store.Segments.Sum(s => (s.End - s.Start).TotalSeconds), 0); // first reading is 1 s in
    }

    [Fact]
    public void Distraction_alert_fires_at_the_threshold_beeps_and_clears_on_refocus()
    {
        var h = Started();
        h.Spend(10, LeetCode);
        h.Spend(119, WhatsApp);
        Assert.Null(h.Last<AlertFired>());
        Assert.Equal(FocusState.Drifting, h.Engine.Snapshot.Focus);

        h.Spend(2, WhatsApp);
        var fired = h.Last<AlertFired>()!;
        Assert.Equal(AlertTrigger.Distraction, fired.Alert.Trigger);
        Assert.Equal("whatsapp", fired.Alert.What);
        Assert.StartsWith("leetcode.com", fired.Alert.LastFocus);
        Assert.Equal(FocusState.FocusLost, h.Engine.Snapshot.Focus);
        Assert.Single(h.Events.OfType<BeepRequested>());

        h.Spend(120, WhatsApp);                   // repeat beep after 2 min
        Assert.Equal(2, h.Events.OfType<BeepRequested>().Count());

        h.Spend(5, LeetCode);
        Assert.NotNull(h.Last<AlertCleared>());
        var stored = Assert.Single(h.Store.Alerts);
        Assert.Equal("distraction", stored.Trigger);
        Assert.Equal("ignored", stored.Response);
        Assert.NotNull(stored.RefocusedAt);
        Assert.Equal(FocusState.Focused, h.Engine.Snapshot.Focus);
    }

    [Fact]
    public void Distraction_alert_fires_even_with_no_input()
    {
        // Found in the live test: sitting on a distraction without typing used to start an idle pause,
        // which reset the distraction timer, so the alert never came.
        var h = Started();
        h.Spend(10, LeetCode);
        h.Spend(125, WhatsApp, typing: false);
        Assert.Equal(AlertTrigger.Distraction, h.Last<AlertFired>()!.Alert.Trigger);
        Assert.Single(h.Events.OfType<AlertFired>());
    }

    [Fact]
    public void Beeps_stop_after_the_maximum_repeats_and_after_back_to_it()
    {
        var h = Started();
        h.SpendMinutes(20, WhatsApp);
        Assert.Equal(1 + h.Settings.Alerts.Sound.MaxRepeats, h.Events.OfType<BeepRequested>().Count());

        var h2 = Started();
        h2.SpendMinutes(2.1, WhatsApp);
        h2.Engine.Respond(AlertResponse.Back);
        h2.SpendMinutes(10, WhatsApp);
        Assert.Single(h2.Events.OfType<BeepRequested>());
        Assert.Equal(FocusState.FocusLost, h2.Engine.Snapshot.Focus); // still flashing until back on the focus list
        Assert.Equal("back", h2.Store.Alerts[0].Response);
    }

    [Fact]
    public void No_input_alert_and_im_reading_counts_the_pause_as_prep()
    {
        var h = Started();
        h.Spend(10, LeetCode);
        h.Spend(305, LeetCode, typing: false);
        Assert.Equal(AlertTrigger.NoInput, h.Last<AlertFired>()!.Alert.Trigger);

        h.Engine.Respond(AlertResponse.Reading);
        Assert.Equal(FocusState.Focused, h.Engine.Snapshot.Focus);
        h.Spend(60, LeetCode, typing: false);
        Assert.Single(h.Events.OfType<AlertFired>());
        h.Spend(5, LeetCode);
        h.Engine.StopSession();

        var gap = Assert.Single(h.Store.Gaps);
        Assert.Equal("reading", gap.Label);
        Assert.Equal("reading", h.Store.Alerts[0].Response);
        var prep = h.Store.SumPrep("interview-prep", h.Store.Sessions[0].Start, h.Clock.Now);
        Assert.InRange(prep.TotalSeconds, 379, 381); // everything except nothing: focus + the reading pause
    }

    [Fact]
    public void Off_plan_site_suggests_the_other_plan_then_alerts_and_switching_clears_it()
    {
        var h = Started();
        var hf = Site("huggingface.co", "Models - Hugging Face");
        h.Spend(10, LeetCode);
        h.Spend(181, hf);
        var suggestion = h.Last<PlanSuggested>()!;
        Assert.Equal("learning-ai", suggestion.Slug);

        h.Spend(120, hf);
        Assert.Equal(AlertTrigger.NeitherList, h.Last<AlertFired>()!.Alert.Trigger);

        h.Engine.SwitchPlan("learning-ai");
        h.Spend(3, hf);
        Assert.NotNull(h.Last<AlertCleared>());
        h.Engine.StopSession();
        Assert.Contains(h.Store.Segments, s => s.PlanSlug == "learning-ai" && s.List == "focus" && s.Topic == "Fine-tuning");
    }

    [Fact]
    public void Lock_pauses_and_a_long_lock_ends_the_session_at_the_moment_of_locking()
    {
        var h = Started();
        h.Spend(30, LeetCode);
        h.Engine.OnLock();
        Assert.Equal(FocusState.Paused, h.Engine.Snapshot.Focus);
        h.SpendMinutes(10, null, typing: false);
        h.Engine.OnUnlock();
        Assert.Equal(SessionState.Running, h.Engine.Snapshot.Session);
        Assert.Equal("locked", h.Store.Gaps.Single().Label);
        Assert.Empty(h.Store.Alerts);

        h.Spend(30, LeetCode);
        h.Engine.OnLock();
        var lockedAt = h.Clock.Now;
        h.SpendMinutes(31, null, typing: false);

        var session = h.Store.Sessions.Single();
        Assert.Equal("lock_timeout", session.EndReason);
        Assert.Equal(lockedAt, session.End);
        Assert.False(h.Engine.Snapshot.IsRunning);
    }

    [Fact]
    public void A_gap_between_ticks_means_the_pc_slept_and_ends_the_session_at_the_last_tick()
    {
        var h = Started();
        h.Spend(30, LeetCode);
        var lastTick = h.Clock.Now;
        h.Clock.Advance(TimeSpan.FromHours(2));
        h.Engine.Tick(new Sample(h.Clock.Now, LeetCode, TimeSpan.Zero));

        var session = h.Store.Sessions.Single();
        Assert.Equal("sleep", session.EndReason);
        Assert.Equal(lastTick, session.End);
        Assert.Equal(EndReason.Sleep, h.Last<SessionEnded>()!.Reason);
        Assert.Equal(lastTick, h.Store.Segments.Single().End);
    }

    [Theory]
    [InlineData(true, "sleep")]
    [InlineData(false, "shutdown")]
    public void Sleep_and_shutdown_events_end_the_session(bool sleep, string reason)
    {
        var h = Started();
        h.Spend(20, LeetCode);
        if (sleep) h.Engine.OnSuspend(); else h.Engine.OnShutdown();
        Assert.Equal(reason, h.Store.Sessions.Single().EndReason);
        Assert.Single(h.Store.Segments);
    }

    [Fact]
    public void Break_runs_for_its_length_then_resumes()
    {
        var h = Started();
        h.Spend(10, LeetCode);
        h.Engine.StartBreak();
        Assert.Equal(FocusState.OnBreak, h.Engine.Snapshot.Focus);
        h.SpendMinutes(10, WhatsApp);             // no alerts while on a break
        Assert.NotNull(h.Last<BreakOver>());
        Assert.Empty(h.Store.Alerts);
        Assert.Equal("break", h.Store.Gaps.Single().Label);
        Assert.Equal(SessionState.Running, h.Engine.Snapshot.Session);
    }

    [Fact]
    public void Open_windows_are_tracked_and_open_distractions_are_flagged_once()
    {
        var h = Started();
        h.Engine.OnOpenWindows(["whatsapp.exe", "notepad.exe", "chrome.exe", "outlook.exe"]);
        var found = h.Last<OpenDistractionsFound>()!;
        Assert.Equal(new[] { "whatsapp" }, found.Apps);

        h.Spend(30, App("notepad.exe", "notes.txt - Notepad"));
        h.Engine.OnOpenWindows(["whatsapp.exe", "notepad.exe", "chrome.exe", "outlook.exe"]);
        Assert.Single(h.Events.OfType<OpenDistractionsFound>());
        h.Engine.StopSession();

        var apps = h.Store.Apps.Values.ToDictionary(a => a.Process);
        Assert.Equal("distraction", apps["whatsapp.exe"].List);
        Assert.Equal("focus", apps["notepad.exe"].List);
        Assert.Equal("browser", apps["chrome.exe"].List);
        Assert.Equal("neither", apps["outlook.exe"].List);
        Assert.Equal(30, apps["outlook.exe"].OpenSeconds, 0);
        Assert.Equal(29, apps["notepad.exe"].FocusSeconds, 0);
    }

    [Fact]
    public void Heartbeat_is_written_every_15_seconds()
    {
        var h = Started();
        h.Spend(61, LeetCode);
        Assert.Equal(4, h.Store.Heartbeats);
    }

    [Fact]
    public void Our_own_windows_dont_change_what_counts_as_in_focus()
    {
        var h = Started();
        h.Spend(10, LeetCode);
        for (var i = 0; i < 10; i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            h.Engine.Tick(new Sample(h.Clock.Now, App("preppulse.exe"), TimeSpan.Zero, OwnWindow: true));
        }
        h.Engine.StopSession();
        var seg = Assert.Single(h.Store.Segments);
        Assert.Equal("leetcode.com", seg.Domain);
        Assert.Equal(19, (seg.End - seg.Start).TotalSeconds, 0);
    }

    [Fact]
    public void Only_one_session_at_a_time_and_unknown_plans_are_rejected()
    {
        var h = Started();
        Assert.False(h.Engine.StartSession("learning-ai"));
        var fresh = new EngineHarness();
        Assert.Throws<InvalidOperationException>(() => fresh.Engine.StartSession("nope"));
    }
}
