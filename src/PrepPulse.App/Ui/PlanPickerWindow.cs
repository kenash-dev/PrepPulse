using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PrepPulse.Core.Plans;

namespace PrepPulse.App.Ui;

public sealed record PlanChoice(Plan Plan, string Detail);

/// <summary>Shown when PrepPulse starts (and from the tray): pick which study plan this session counts toward.</summary>
internal sealed class PlanPickerWindow : Window
{
    private readonly List<(RadioButton Radio, Plan Plan)> _options = [];
    private readonly Action<string> _start;

    public PlanPickerWindow(IReadOnlyList<PlanChoice> choices, string? defaultSlug, IReadOnlyList<string> problems, string plansDir,
        Action<string> start, Action openFolder)
    {
        _start = start;
        Title = "PrepPulse — start a study session";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = Theme.Background;
        Icon = IconFactory.WindowIcon();

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        panel.Children.Add(Theme.Text("Start a study session", 18, bold: true));
        panel.Children.Add(new TextBlock
        {
            Text = "Time and focus will count toward the plan you pick. You can switch plans from the tray later.",
            Foreground = Theme.Ink2, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
        });

        if (choices.Count == 0)
        {
            panel.Children.Add(Theme.Text($"No study plans yet. Add a .yaml plan file to {plansDir} — there's a README in that folder.", 13, Theme.Ink2));
        }

        var selected = choices.FirstOrDefault(c => c.Plan.Slug == defaultSlug) ?? choices.FirstOrDefault();
        foreach (var c in choices)
        {
            var header = new StackPanel();
            header.Children.Add(Theme.Text(c.Plan.Name, 14, bold: true));
            header.Children.Add(Theme.Text(c.Detail, 12, Theme.Muted));
            var radio = new RadioButton
            {
                Content = header,
                GroupName = "plan",
                IsChecked = ReferenceEquals(c, selected),
                Margin = new Thickness(0, 6, 0, 6),
                Foreground = Theme.Ink,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            radio.MouseDoubleClick += (_, _) => Start();
            _options.Add((radio, c.Plan));
            panel.Children.Add(radio);
        }

        foreach (var p in problems)
            panel.Children.Add(new TextBlock { Text = "⚠ " + p, Foreground = Theme.Crit, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        var startButton = Theme.Button("Start", Start, primary: true);
        startButton.IsDefault = true;
        startButton.IsEnabled = choices.Count > 0;
        var later = Theme.Button("Not now", Close);
        later.IsCancel = true;
        var buttons = Theme.Row(startButton, later, Theme.Button("Open plans folder", openFolder));
        buttons.Margin = new Thickness(0, 16, 0, 0);
        panel.Children.Add(buttons);
        Content = panel;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Start()
    {
        var chosen = _options.FirstOrDefault(o => o.Radio.IsChecked == true);
        if (chosen.Plan is null) return;
        Close();
        _start(chosen.Plan.Slug);
    }
}
