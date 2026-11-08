using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using PrepPulse.App.Interop;
using PrepPulse.Core;
using PrepPulse.Core.Engine;
using PrepPulse.Core.Matching;

namespace PrepPulse.App.Ui;

/// <summary>
/// The small window that owns the taskbar button while a session runs. The taskbar progress bar fills toward today's
/// target and is green (focused), yellow (drifting/idle) or red (focus lost, and the button flashes).
/// Hovering the button shows Pause · Break · Stop. Closing it just minimises it.
/// </summary>
internal sealed class SessionWindow : Window
{
    private readonly Shell _shell;
    private readonly TextBlock _plan = Theme.Text("", 15, bold: true);
    private readonly TextBlock _state = Theme.Text("", 13);
    private readonly Ellipse _dot = Theme.Dot(Theme.Good);
    private readonly TextBlock _clock = Theme.Text("0:00:00", 30, bold: true);
    private readonly TextBlock _now = Theme.Text("", 13, Theme.Ink2);
    private readonly TextBlock _today = Theme.Text("", 13, Theme.Ink2);
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 12), BorderThickness = new Thickness(0) };
    private readonly Button _pause;
    private readonly Button _break;
    private readonly ThumbButtonInfo _thumbPause;
    private readonly ThumbButtonInfo _thumbBreak;
    private readonly ImageSource _pauseGlyph = Glyph("M3,2 h3.5 v12 h-3.5 Z M9.5,2 h3.5 v12 h-3.5 Z");
    private readonly ImageSource _playGlyph = Glyph("M4,2 L14,8 L4,14 Z");
    private bool _allowClose;
    private bool _flashing;

    public SessionWindow(Shell shell)
    {
        _shell = shell;
        Title = "PrepPulse";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        ShowActivated = false;
        Background = Theme.Background;
        Icon = IconFactory.WindowIcon();
        WindowStartupLocation = WindowStartupLocation.Manual;

        _pause = Theme.Button("Pause", () => _shell.TogglePause());
        _break = Theme.Button("Break", () => _shell.ToggleBreak());

        var panel = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        panel.Children.Add(_plan);
        panel.Children.Add(Theme.Row(_dot, _state));
        panel.Children.Add(_clock);
        panel.Children.Add(_now);
        panel.Children.Add(_today);
        _bar.Background = Theme.Track;
        panel.Children.Add(_bar);
        var buttons = Theme.Row(_pause, _break,
            Theme.Button("Stop", () => _shell.Engine.StopSession(), primary: true),
            Theme.Button("Dashboard", () => _shell.OpenDashboard()));
        panel.Children.Add(buttons);
        panel.Children.Add(Theme.Text($"{_shell.HotkeyText} starts/stops · locking pauses · sleep or restart ends the session", 11, Theme.Muted));
        Content = panel;

        _thumbPause = Thumb(_pauseGlyph, "Pause", () => _shell.TogglePause());
        _thumbBreak = Thumb(Glyph("M2,6 h10 v4 a4,4 0 0 1 -4,4 h-2 a4,4 0 0 1 -4,-4 Z M12,7 h2 v3 h-2 Z"), "Take a break", () => _shell.ToggleBreak());
        TaskbarItemInfo = new TaskbarItemInfo { ProgressState = TaskbarItemProgressState.Normal, ProgressValue = 0.03 };
        TaskbarItemInfo.ThumbButtonInfos.Add(_thumbPause);
        TaskbarItemInfo.ThumbButtonInfos.Add(_thumbBreak);
        TaskbarItemInfo.ThumbButtonInfos.Add(Thumb(Glyph("M3,3 h10 v10 h-10 Z"), "Stop session", () => _shell.Engine.StopSession()));

        Loaded += (_, _) => Theme.PlaceBottomRight(this);
    }

    public void Render(EngineSnapshot s)
    {
        if (!s.IsRunning) return;
        _plan.Text = s.PlanName ?? "";
        _dot.Fill = Theme.ForState(s.Focus);
        _state.Text = StateText(s);
        _clock.Text = s.SessionLength.ToString(@"h\:mm\:ss");
        _now.Text = s.Current is null ? "Now: waiting for the first reading…" : $"Now: {s.Current}{(s.CurrentTopic is null ? "" : $" · {s.CurrentTopic}")}";
        _today.Text = $"Today {Durations.Format(s.PrepToday)} of {Durations.Format(s.TargetToday)}";
        _bar.Value = s.TodayProgress * 100;
        _bar.Foreground = Theme.ForState(s.Focus);

        var paused = s.Session is SessionState.Paused or SessionState.Locked;
        _pause.Content = paused ? "Resume" : "Pause";
        _break.Content = s.Session == SessionState.OnBreak ? "End break" : "Break";
        _thumbPause.ImageSource = paused ? _playGlyph : _pauseGlyph;
        _thumbPause.Description = paused ? "Resume" : "Pause";
        _thumbBreak.Description = s.Session == SessionState.OnBreak ? "End break" : "Take a break";

        Title = $"PrepPulse · {s.SessionLength:h\\:mm} · {s.PlanName}";
        TaskbarItemInfo.ProgressValue = Math.Max(0.03, s.TodayProgress);
        TaskbarItemInfo.ProgressState = s.Focus switch
        {
            FocusState.Focused => TaskbarItemProgressState.Normal,
            FocusState.FocusLost => TaskbarItemProgressState.Error,
            _ => TaskbarItemProgressState.Paused,
        };
    }

    /// <summary>Flashes the taskbar button until turned off.</summary>
    public void SetFlash(bool on)
    {
        if (on == _flashing) return;
        _flashing = on;
        Native.Flash(new WindowInteropHelper(this).Handle, on);
    }

    public void ShowForSession(bool minimized)
    {
        if (!IsVisible)
        {
            WindowState = minimized ? WindowState.Minimized : WindowState.Normal;
            Show();
        }
    }

    public void Restore()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void HideForEnd()
    {
        SetFlash(false);
        Hide();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
        }
        base.OnClosing(e);
    }

    private static string StateText(EngineSnapshot s) => s.Focus switch
    {
        FocusState.Focused => s.CurrentTopic is null ? "Focused" : $"Focused · {s.CurrentTopic}",
        FocusState.Idle => $"No input for {Durations.Spoken(s.IdleFor)}",
        FocusState.FocusLost => "Focus lost",
        FocusState.Paused => s.Session == SessionState.Locked ? "Paused while the PC is locked" : "Paused",
        FocusState.OnBreak => s.BreakUntil is { } until ? $"On a break until {until:HH:mm}" : "On a break",
        _ => s.CurrentList == ListKind.Distraction ? "On a distraction" : "Not in this plan's focus list",
    };

    private static ThumbButtonInfo Thumb(ImageSource image, string description, Action onClick)
    {
        var t = new ThumbButtonInfo { ImageSource = image, Description = description, DismissWhenClicked = false };
        t.Click += (_, _) => onClick();
        return t;
    }

    private static ImageSource Glyph(string path)
    {
        var drawing = new GeometryDrawing(Brushes.White, null, Geometry.Parse(path));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
