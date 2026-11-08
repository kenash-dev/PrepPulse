using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PrepPulse.App.Interop;
using PrepPulse.Core.Engine;

namespace PrepPulse.App.Ui;

/// <summary>
/// Draws the tray icon at runtime: a ring that fills with today's progress, in the colour of the current focus state,
/// with a dot in the middle. Icons are cached per (progress step, state, dimmed).
/// </summary>
internal static class IconFactory
{
    private static readonly Dictionary<string, Icon> Cache = new();

    public static Color ColourFor(FocusState state) => state switch
    {
        FocusState.Focused => Color.FromArgb(0x0C, 0xA3, 0x0C),
        FocusState.Drifting or FocusState.Idle => Color.FromArgb(0xFA, 0xB2, 0x19),
        FocusState.FocusLost => Color.FromArgb(0xD0, 0x3B, 0x3B),
        FocusState.NoSession => Color.FromArgb(0x2A, 0x78, 0xD6),
        _ => Color.FromArgb(0x8F, 0x8D, 0x86),
    };

    public static Icon Tray(double progress, FocusState state, bool dim)
    {
        var step = (int)Math.Round(Math.Clamp(progress, 0, 1) * 24);
        var key = $"{step}|{state}|{dim}";
        if (!Cache.TryGetValue(key, out var icon))
        {
            icon = Draw(step / 24.0, ColourFor(state), dim, state == FocusState.NoSession);
            Cache[key] = icon;
        }
        return icon;
    }

    public static System.Windows.Media.ImageSource WindowIcon()
    {
        using var icon = Draw(0.75, ColourFor(FocusState.NoSession), false, false);
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static Icon Draw(double progress, Color colour, bool dim, bool idleLook)
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var ring = new RectangleF(3.5f, 3.5f, 25f, 25f);
            using (var track = new Pen(Color.FromArgb(dim ? 70 : 150, 150, 150, 150), 5f))
                g.DrawEllipse(track, ring);
            if (progress > 0.001 && !idleLook)
            {
                using var arc = new Pen(dim ? Color.FromArgb(90, colour) : colour, 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, ring, -90f, (float)(360 * Math.Min(1, progress)));
            }
            using var dot = new SolidBrush(dim ? Color.FromArgb(60, colour) : colour);
            g.FillEllipse(dot, 10.5f, 10.5f, 11f, 11f);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }
}
