using System.Globalization;
using System.Text.RegularExpressions;

namespace PrepPulse.Core;

/// <summary>Parses and formats the durations used in plan files: 2h, 90m, 1h30m, 1.5h, or a bare number of minutes.</summary>
public static partial class Durations
{
    [GeneratedRegex(@"^\s*(?:(?<h>\d+(?:\.\d+)?)\s*h(?:ours?|rs?)?)?\s*(?:(?<m>\d+(?:\.\d+)?)\s*m(?:in(?:utes?|s)?)?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();

        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var plainMinutes))
        {
            if (plainMinutes < 0) return false;
            value = TimeSpan.FromMinutes(plainMinutes);
            return true;
        }

        var m = Pattern().Match(t);
        if (!m.Success || (!m.Groups["h"].Success && !m.Groups["m"].Success)) return false;

        double hours = m.Groups["h"].Success ? double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        double minutes = m.Groups["m"].Success ? double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        value = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        return true;
    }

    public static TimeSpan Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a duration. Use e.g. 2h, 90m or 1h30m.");

    /// <summary>45m, 2h, 2h 05m.</summary>
    public static string Format(TimeSpan t)
    {
        var total = (int)Math.Round(t.TotalMinutes, MidpointRounding.AwayFromZero);
        if (total < 0) total = 0;
        int h = total / 60, m = total % 60;
        if (h == 0) return $"{m}m";
        return m == 0 ? $"{h}h" : $"{h}h {m:00}m";
    }

    /// <summary>"30 seconds", "1 minute", "5 minutes" — for alert text.</summary>
    public static string Spoken(TimeSpan t)
    {
        if (t.TotalSeconds < 119.5) return $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} seconds";
        var minutes = (int)Math.Round(t.TotalMinutes);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
