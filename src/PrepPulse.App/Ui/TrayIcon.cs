using System;
using System.Drawing;
using System.Linq;
using PrepPulse.Core;
using PrepPulse.Core.Engine;
using Forms = System.Windows.Forms;

namespace PrepPulse.App.Ui;

/// <summary>
/// The icon in the notification area by the clock. The ring fills with today's progress and takes the colour of the
/// focus state; it blinks red when focus is lost. Left-click opens the session window (or the plan picker),
/// double-click opens the dashboard, right-click shows the menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Shell _shell;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.Timer _blink;
    private EngineSnapshot _last = EngineSnapshot.Empty;
    private bool _blinkDim;
    private Action? _balloonAction;

    public TrayIcon(Shell shell)
    {
        _shell = shell;
        _menu = new Forms.ContextMenuStrip();
        _menu.Opening += (_, e) => { Build(); e.Cancel = false; };
        _icon = new Forms.NotifyIcon
        {
            Icon = IconFactory.Tray(0, FocusState.NoSession, false),
            Text = "PrepPulse — no session",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _shell.Show(); };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _shell.OpenDashboard(); };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonAction = null;
        _blink = new Forms.Timer { Interval = 500 };
        _blink.Tick += (_, _) => { _blinkDim = !_blinkDim; UpdateIcon(); };
        Build();
    }

    public void Render(EngineSnapshot s, bool flash)
    {
        _last = s;
        if (s.Focus == FocusState.FocusLost && flash)
        {
            if (!_blink.Enabled) _blink.Start();
        }
        else if (_blink.Enabled)
        {
            _blink.Stop();
            _blinkDim = false;
        }
        UpdateIcon();
        _icon.Text = Tooltip(s);
    }

    public void Balloon(string title, string text, Action? onClick = null, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(5000, title, text, icon);
    }

    private void UpdateIcon() => _icon.Icon = IconFactory.Tray(_last.TodayProgress, _last.Focus, _blinkDim);

    private static string Tooltip(EngineSnapshot s)
    {
        if (!s.IsRunning) return "PrepPulse — no session (click to start)";
        var state = s.Focus switch
        {
            FocusState.Focused => "Focused",
            FocusState.Idle => "No input",
            FocusState.FocusLost => "Focus lost",
            FocusState.Paused => s.Session == SessionState.Locked ? "PC locked" : "Paused",
            FocusState.OnBreak => "On a break",
            _ => "Off plan",
        };
        var text = $"PrepPulse · {s.PlanName}\n{state}{(s.Current is null ? "" : " · " + s.Current)}\nToday {Durations.Format(s.PrepToday)} / {Durations.Format(s.TargetToday)} · {s.SessionLength:h\\:mm\\:ss}";
        return text.Length <= 127 ? text : text[..126] + "…";
    }

    private void Build()
    {
        _menu.Items.Clear();
        var s = _shell.Engine.Snapshot;
        var catalog = _shell.Catalog.Catalog;

        _menu.Items.Add(new Forms.ToolStripMenuItem(s.IsRunning ? $"{s.PlanName} · {s.SessionLength:h\\:mm\\:ss}" : "No session running") { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());

        if (!s.IsRunning)
        {
            var start = new Forms.ToolStripMenuItem("Start session");
            var last = _shell.LastPlanSlug;
            foreach (var p in catalog.Plans.Where(p => !p.Paused))
            {
                var item = new Forms.ToolStripMenuItem(p.Name, null, (_, _) => _shell.StartSession(p.Slug));
                if (p.Slug == last) item.Font = new Font(item.Font, FontStyle.Bold);
                start.DropDownItems.Add(item);
            }
            if (start.DropDownItems.Count == 0)
                start.DropDownItems.Add(new Forms.ToolStripMenuItem("No plans yet — open the plans folder", null, (_, _) => _shell.OpenPlansFolder()));
            _menu.Items.Add(start);
            _menu.Items.Add(new Forms.ToolStripMenuItem("Choose a plan…", null, (_, _) => _shell.ShowPicker()));
        }
        else
        {
            var paused = s.Session is SessionState.Paused or SessionState.Locked;
            _menu.Items.Add(new Forms.ToolStripMenuItem(paused ? "Resume" : "Pause", null, (_, _) => _shell.TogglePause()));
            var onBreak = s.Session == SessionState.OnBreak;
            _menu.Items.Add(new Forms.ToolStripMenuItem(onBreak ? "End break" : $"Take a {_shell.Catalog.Settings.Alerts.BreakMinutes:0.#}-min break", null,
                (_, _) => _shell.ToggleBreak()));
            var switchTo = new Forms.ToolStripMenuItem("Switch plan");
            foreach (var p in catalog.Plans.Where(p => !p.Paused && p.Slug != s.PlanSlug))
                switchTo.DropDownItems.Add(new Forms.ToolStripMenuItem(p.Name, null, (_, _) => _shell.Engine.SwitchPlan(p.Slug)));
            switchTo.Enabled = switchTo.DropDownItems.Count > 0;
            _menu.Items.Add(switchTo);
            _menu.Items.Add(new Forms.ToolStripMenuItem("Stop session", null, (_, _) => _shell.Engine.StopSession()));
            _menu.Items.Add(new Forms.ToolStripMenuItem("Show session window", null, (_, _) => _shell.Show()));
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("Open dashboard", null, (_, _) => _shell.OpenDashboard()));
        _menu.Items.Add(new Forms.ToolStripMenuItem("Open plans folder", null, (_, _) => _shell.OpenPlansFolder()));
        _menu.Items.Add(new Forms.ToolStripMenuItem("Edit distraction list", null, (_, _) => _shell.EditFile(_shell.Paths.DistractionsFile)));
        _menu.Items.Add(new Forms.ToolStripMenuItem("Edit settings", null, (_, _) => _shell.EditFile(_shell.Paths.SettingsFile)));
        _menu.Items.Add(new Forms.ToolStripMenuItem("Reload plans and settings", null, (_, _) => _shell.Catalog.Reload()));

        var problems = _shell.Catalog.Problems;
        if (problems.Count > 0)
        {
            _menu.Items.Add(new Forms.ToolStripMenuItem($"⚠ {problems.Count} problem{(problems.Count == 1 ? "" : "s")} in your plan files…", null,
                (_, _) => _shell.ShowProblems()));
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        if (_shell.Paths.IsDefault)
        {
            _menu.Items.Add(new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => StartWithWindows.Set(!StartWithWindows.IsEnabled()))
            {
                Checked = StartWithWindows.IsEnabled(),
            });
        }
        _menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => _shell.Exit()));
    }

    public void Dispose()
    {
        _blink.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
