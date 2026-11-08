using System;
using System.Windows;
using System.Windows.Controls;
using PrepPulse.Core;
using PrepPulse.Core.Engine;

namespace PrepPulse.App.Ui;

/// <summary>
/// The focus-lost popup in the bottom-right corner. Topmost, doesn't steal focus, and stays until you answer or
/// get back to something on your focus list. Unlike a Windows notification it isn't hidden by Do Not Disturb.
/// </summary>
internal sealed class AlertWindow : Window
{
    public AlertWindow(ActiveAlertInfo alert, AppSettings settings, Action<AlertResponse> respond)
    {
        AlertId = alert.Id;
        Title = "PrepPulse — focus lost";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Theme.Surface;
        BorderBrush = Theme.Crit;
        BorderThickness = new Thickness(1, 4, 1, 1);

        var lost = alert.FiredAt - alert.Since;
        var (headline, body) = alert.Trigger switch
        {
            AlertTrigger.Distraction => ($"{Cap(Durations.Spoken(lost))} on a distraction",
                $"{alert.What ?? "This app"} is on your distraction list (since {alert.Since:HH:mm})."),
            AlertTrigger.NoInput => ($"No input for {Durations.Spoken(lost)}",
                "Still with it? If you're reading or thinking, say so and it counts."),
            _ => ($"{Cap(Durations.Spoken(lost))} off your plan",
                $"{alert.What ?? "This app"} isn't in {alert.PlanName}'s focus list."),
        };
        if (alert.LastFocus is { } last) body += $" Last focus: {last}.";

        var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        panel.Children.Add(Theme.Text($"PrepPulse · {alert.FiredAt:HH:mm} · {alert.PlanName}", 11, Theme.Muted));
        panel.Children.Add(new TextBlock
        {
            Text = "▲ " + headline,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Ink,
            Margin = new Thickness(0, 4, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Theme.Text(body, 13, Theme.Ink2));

        var breakText = $"Break {settings.Alerts.BreakMinutes:0.#} min";
        var buttons = alert.Trigger == AlertTrigger.NoInput
            ? Theme.Row(
                Theme.Button("I'm reading", () => Answer(respond, AlertResponse.Reading), primary: true),
                Theme.Button(breakText, () => Answer(respond, AlertResponse.Break)),
                Theme.Button("End session", () => Answer(respond, AlertResponse.End)))
            : Theme.Row(
                Theme.Button("Back to it", () => Answer(respond, AlertResponse.Back), primary: true),
                Theme.Button(breakText, () => Answer(respond, AlertResponse.Break)),
                Theme.Button("End session", () => Answer(respond, AlertResponse.End)));
        buttons.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => Theme.PlaceBottomRight(this);
    }

    public long AlertId { get; }

    private void Answer(Action<AlertResponse> respond, AlertResponse response)
    {
        Close();
        respond(response);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
