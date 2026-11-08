using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using PrepPulse.Core.Engine;

namespace PrepPulse.App.Ui;

/// <summary>Colours that follow Windows light/dark mode, plus small builders for the code-only WPF windows.</summary>
internal static class Theme
{
    public static readonly bool Dark = ReadDark();

    public static readonly Brush Background = B(Dark ? "#1F1F1E" : "#F9F9F7");
    public static readonly Brush Surface = B(Dark ? "#2A2A28" : "#FFFFFF");
    public static readonly Brush Ink = B(Dark ? "#F0EFEC" : "#0B0B0B");
    public static readonly Brush Ink2 = B(Dark ? "#C3C2B7" : "#52514E");
    public static readonly Brush Muted = B("#898781");
    public static readonly Brush Border = B(Dark ? "#3A3A37" : "#E1E0D9");
    public static readonly Brush Accent = B(Dark ? "#3987E5" : "#2A78D6");
    public static readonly Brush Good = B("#0CA30C");
    public static readonly Brush Warn = B("#FAB219");
    public static readonly Brush Crit = B("#D03B3B");
    public static readonly Brush Grey = B("#8F8D86");
    public static readonly Brush Track = B(Dark ? "#104281" : "#CDE2FB");

    public static Brush ForState(FocusState state) => state switch
    {
        FocusState.Focused => Good,
        FocusState.Drifting or FocusState.Idle => Warn,
        FocusState.FocusLost => Crit,
        FocusState.NoSession => Accent,
        _ => Grey,
    };

    private static bool ReadDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    private static SolidColorBrush B(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ---------------------------------------------------------------- builders

    public static TextBlock Text(string text, double size = 13, Brush? colour = null, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = colour ?? Ink,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    public static Button Button(string text, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13,
            Foreground = primary ? Brushes.White : Ink,
            Background = primary ? Accent : Surface,
            BorderBrush = primary ? Accent : Border,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Ellipse Dot(Brush fill, double size = 10) => new()
    {
        Width = size,
        Height = size,
        Fill = fill,
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static void PlaceBottomRight(Window w, double margin = 16)
    {
        var area = SystemParameters.WorkArea;
        w.Left = area.Right - w.ActualWidth - margin;
        w.Top = area.Bottom - w.ActualHeight - margin;
    }
}
