using System.Globalization;

namespace PrepPulse.Core;

/// <summary>Small rules that keep PrepPulse light in the background. Pure functions, so they're unit tested.</summary>
public static class Housekeeping
{
    /// <summary>Daily log files are kept this long.</summary>
    public const int KeepLogDays = 30;

    /// <summary>While you've been idle past the idle threshold, the sampler checks this often instead of every second.</summary>
    public static readonly TimeSpan IdleSampleEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The log files (named preppulse-yyyyMMdd.log) older than <paramref name="keepDays"/> days. Files with any
    /// other name are never returned, so nothing else in the folder can be deleted by mistake.
    /// </summary>
    public static IReadOnlyList<string> ExpiredLogs(IEnumerable<string> paths, DateOnly today, int keepDays = KeepLogDays)
    {
        var cutoff = today.AddDays(-keepDays);
        var expired = new List<string>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("preppulse-", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
            var stamp = name["preppulse-".Length..^".log".Length];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
                expired.Add(path);
        }
        return expired;
    }

    /// <summary>
    /// Whether the sampler should take a sample now. Every second while you're active; every
    /// <see cref="IdleSampleEvery"/> once you've been idle past <paramref name="idleAfter"/>. Accuracy doesn't
    /// suffer: idle pauses are dated from your last input, and any input brings sampling back to every second.
    /// </summary>
    public static bool ShouldSample(TimeSpan idleFor, TimeSpan sinceLastSample, TimeSpan idleAfter) =>
        idleFor < idleAfter || sinceLastSample >= IdleSampleEvery;
}
