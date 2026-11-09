using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PrepPulse.App.Services;
using PrepPulse.Core;
using PrepPulse.Core.Engine;
using PrepPulse.Data;
using Forms = System.Windows.Forms;

namespace PrepPulse.App.Ui;

/// <summary>
/// UI controller (runs on the WPF thread). Subscribes to the engine and translates its events into tray, taskbar,
/// popup, sound and balloon updates; forwards Windows lock/sleep/shutdown events to the engine.
/// </summary>
internal sealed class Shell : IShellCommands, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly SqliteStore _store;
    private readonly IClock _clock;
    private readonly int _recoveredSessions;
    private readonly DashboardServer _dashboard;
    private TrayIcon? _tray;
    private SessionWindow? _session;
    private AlertWindow? _alert;
    private PlanPickerWindow? _picker;
    private Hotkey? _hotkey;
    private string? _hotkeySpec;
    private bool _exiting;

    public Shell(FocusEngine engine, CatalogService catalog, SqliteStore store, AppPaths paths, IClock clock, DashboardServer dashboard, int recoveredSessions)
    {
        Engine = engine;
        Catalog = catalog;
        Paths = paths;
        _dashboard = dashboard;
        _store = store;
        _clock = clock;
        _recoveredSessions = recoveredSessions;
        _ui = Dispatcher.CurrentDispatcher;
    }

    public FocusEngine Engine { get; }
    public CatalogService Catalog { get; }
    public AppPaths Paths { get; }
    public string? LastPlanSlug { get; private set; }
    public string HotkeyText => Catalog.Settings.Hotkey;

    private AppSettings Settings => Catalog.Settings;

    public void Start(string? initialCommand, CommandRouter router)
    {
        LastPlanSlug = _store.LastPlanSlug();
        _tray = new TrayIcon(this);

        Engine.Changed += s => _ui.InvokeAsync(() => OnSnapshot(s));
        Engine.Event += e => _ui.InvokeAsync(() => OnEvent(e));
        Catalog.Changed += () => _ui.InvokeAsync(OnCatalogChanged);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;

        RegisterHotkey();

        if (_recoveredSessions > 0)
            _tray.Balloon("PrepPulse", "Your last session wasn't closed properly (crash or power loss). It was ended at its last saved moment.");

        if (Catalog.Problems.Count > 0)
            _tray.Balloon("PrepPulse — check your plan files", string.Join("\n", Catalog.Problems.Take(2)), ShowProblems, Forms.ToolTipIcon.Warning);

        if (initialCommand is not null) router.Handle(initialCommand);
        else if (Settings.ShowPickerOnLaunch) ShowPicker();
        else _tray.Balloon("PrepPulse is running", $"Click the tray icon or press {HotkeyText} to start a session.");
    }

    // ---------------------------------------------------------------- engine → UI

    private void OnSnapshot(EngineSnapshot s)
    {
        if (_exiting) return;
        _tray?.Render(s, Settings.Alerts.FlashTrayIcon);
        if (_session is { } w && s.IsRunning)
        {
            w.Render(s);
            w.SetFlash(s.Focus == FocusState.FocusLost && Settings.Alerts.FlashTaskbar);
        }
    }

    private void OnEvent(EngineEvent e)
    {
        if (_exiting) return;
        switch (e)
        {
            case SessionStarted started:
                LastPlanSlug = started.PlanSlug;
                _picker?.Close();
                _session ??= new SessionWindow(this);
                _session.ShowForSession(Settings.SessionWindowMinimized);
                _tray?.Balloon($"Studying: {started.PlanName}", $"PrepPulse is tracking your focus. {HotkeyText} or the tray icon stops the session.");
                break;

            case SessionEnded ended:
                CloseAlert();
                // Close it rather than hide it: a new one is made for the next session, so nothing sits in memory between sessions.
                _session?.HideForEnd();
                _session?.ForceClose();
                _session = null;
                var prep = Durations.Format(_store.SumPrep(LastPlanSlug ?? "", LocalTime.StartOfDay(LocalTime.DateOf(_clock.Now, _clock.Zone), _clock.Zone), _clock.Now));
                var why = ended.Reason switch
                {
                    EndReason.Sleep => "The PC went to sleep, so the session ended",
                    EndReason.Shutdown => "Windows is restarting or shutting down, so the session ended",
                    EndReason.LockTimeout => "The PC was locked too long, so the session ended",
                    _ => "Session ended",
                };
                _tray?.Balloon($"{ended.PlanName}: {prep} today", $"{why} at {ended.At:HH:mm}. Click to start a new one.", ShowPicker);
                break;

            case AlertFired fired:
                if (Settings.Alerts.Popup)
                {
                    CloseAlert();
                    _alert = new AlertWindow(fired.Alert, Settings, r => Engine.Respond(r));
                    _alert.Show();
                }
                break;

            case AlertCleared cleared:
                if (_alert?.AlertId == cleared.AlertId) CloseAlert();
                break;

            case BeepRequested:
                Sounds.Play(Settings.Alerts.Sound.File);
                break;

            case BreakOver:
                if (Settings.Alerts.Sound.Enabled) Sounds.Play(Settings.Alerts.Sound.File);
                _tray?.Balloon("Break's over", $"Back to {Engine.Snapshot.PlanName}.");
                break;

            case PlanSuggested suggested:
                _tray?.Balloon($"That's in {suggested.Name}", $"You've been on {suggested.What} for a while. Click to switch this session to {suggested.Name}.",
                    () => Engine.SwitchPlan(suggested.Slug));
                break;

            case OpenDistractionsFound found:
                var names = found.Apps.Count == 1 ? found.Apps[0] : string.Join(", ", found.Apps.Take(found.Apps.Count - 1)) + " and " + found.Apps[^1];
                _tray?.Balloon("Distractions are open", $"{names} {(found.Apps.Count == 1 ? "is" : "are")} on your distraction list. Close {(found.Apps.Count == 1 ? "it" : "them")} for a cleaner session?");
                break;
        }
    }

    private void OnCatalogChanged()
    {
        if (Catalog.Settings.Hotkey != _hotkeySpec) RegisterHotkey();
        if (Catalog.Problems.Count > 0)
            _tray?.Balloon("PrepPulse — check your plan files", string.Join("\n", Catalog.Problems.Take(2)), ShowProblems, Forms.ToolTipIcon.Warning);
        OnSnapshot(Engine.Snapshot);
    }

    // ---------------------------------------------------------------- Windows events → engine

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Engine.OnSuspend();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                Engine.OnLock();
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                Engine.OnUnlock();
                break;
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Engine.OnShutdown();

    public void OnWindowsShutdown() => Engine.OnShutdown();

    // ---------------------------------------------------------------- commands

    public void Show()
    {
        if (!_ui.CheckAccess()) { _ui.Invoke(Show); return; }
        if (Engine.Snapshot.IsRunning) _session?.Restore();
        else ShowPicker();
    }

    public void ShowPicker()
    {
        if (Engine.Snapshot.IsRunning) { _session?.Restore(); return; }
        if (_picker is { IsVisible: true })
        {
            _picker.Activate();
            return;
        }
        var choices = Catalog.Catalog.Plans.Where(p => !p.Paused).Select(p => new PlanChoice(p, PlanDetail(p))).ToList();
        _picker = new PlanPickerWindow(choices, LastPlanSlug, Catalog.Problems, Paths.PlansDir, slug => StartSession(slug), OpenPlansFolder);
        _picker.Closed += (_, _) => _picker = null;
        _picker.Show();
        _picker.Activate();
    }

    public void StartSession(string slug)
    {
        try { Engine.StartSession(slug); }
        catch (Exception ex)
        {
            AppLog.Error("Couldn't start session", ex);
            _tray?.Balloon("Couldn't start the session", ex.Message, null, Forms.ToolTipIcon.Error);
        }
    }

    public void TogglePause()
    {
        var s = Engine.Snapshot;
        if (s.Session is SessionState.Paused or SessionState.Locked) Engine.Resume();
        else Engine.Pause();
    }

    public void ToggleBreak()
    {
        if (Engine.Snapshot.Session == SessionState.OnBreak) Engine.EndBreak();
        else Engine.StartBreak();
    }

    public void OpenDashboard()
    {
        try { Launch(_dashboard.EnsureStarted()); }
        catch (Exception ex)
        {
            AppLog.Error("The dashboard couldn't start", ex);
            _tray?.Balloon("Dashboard", $"The dashboard couldn't start: {ex.Message}");
        }
    }


    public void OpenPlansFolder() => Launch(Paths.PlansDir);

    public void EditFile(string path)
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = false }); }
        catch (Exception ex) { AppLog.Warn($"Couldn't open {path}", ex); }
    }

    public void ShowProblems()
    {
        var text = Catalog.Problems.Count == 0 ? "No problems found." : string.Join("\n\n", Catalog.Problems);
        MessageBox.Show(text, "PrepPulse — plan file problems", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Exit()
    {
        if (!_ui.CheckAccess()) { _ui.Invoke(Exit); return; }
        if (_exiting) return;
        Engine.StopSession(EndReason.AppExit);
        _exiting = true;
        Application.Current.Shutdown();
    }

    private void ToggleFromHotkey()
    {
        if (Engine.Snapshot.IsRunning) Engine.StopSession();
        else if (LastPlanSlug is { } last && Catalog.Catalog.Find(last) is { Paused: false }) StartSession(last);
        else ShowPicker();
    }

    private void RegisterHotkey()
    {
        _hotkey?.Dispose();
        _hotkeySpec = Settings.Hotkey;
        _hotkey = new Hotkey(_hotkeySpec, ToggleFromHotkey);
    }

    private string PlanDetail(Core.Plans.Plan p)
    {
        var today = LocalTime.DateOf(_clock.Now, _clock.Zone);
        if (p.Weekly is { } weekly)
        {
            var ws = LocalTime.WeekStart(today);
            var done = _store.SumPrep(p.Slug, LocalTime.StartOfDay(ws, _clock.Zone), _clock.Now);
            return $"This week {Durations.Format(done)} of {Durations.Format(weekly)}";
        }
        var doneToday = _store.SumPrep(p.Slug, LocalTime.StartOfDay(today, _clock.Zone), _clock.Now);
        var left = p.End is { } end ? $" · {Math.Max(0, end.DayNumber - today.DayNumber)} days left" : "";
        return $"Today {Durations.Format(doneToday)} of {Durations.Format(p.ExpectedOn(today))}{left}";
    }

    private void CloseAlert()
    {
        _alert?.Close();
        _alert = null;
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn($"Couldn't open {target}", ex); }
    }

    public void Dispose()
    {
        _exiting = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _hotkey?.Dispose();
        CloseAlert();
        _session?.ForceClose();
        _tray?.Dispose();
    }
}
