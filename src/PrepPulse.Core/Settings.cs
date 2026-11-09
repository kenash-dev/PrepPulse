using System.Text.Json;

namespace PrepPulse.Core;

/// <summary>User settings, read from settings.json in the config folder. Minutes may be fractions (0.5 = 30 s).</summary>
public sealed class AppSettings
{
    public double IdleAfterMinutes { get; set; } = 2;
    public AlertSettings Alerts { get; set; } = new();
    public SessionSettings Session { get; set; } = new();
    public int DashboardPort { get; set; } = 5317;
    /// <summary>The dashboard's web server starts when you open the dashboard and stops after this many minutes unused. 0 = keep it running.</summary>
    public double DashboardIdleMinutes { get; set; } = 10;
    public string Hotkey { get; set; } = "Ctrl+Alt+P";
    public bool ShowPickerOnLaunch { get; set; } = true;
    public bool SessionWindowMinimized { get; set; } = true;
}

public sealed class AlertSettings
{
    public double NoInputMinutes { get; set; } = 5;
    public double DistractionMinutes { get; set; } = 2;
    public double NeitherListMinutes { get; set; } = 5;
    public bool FlashTrayIcon { get; set; } = true;
    public bool FlashTaskbar { get; set; } = true;
    public bool Popup { get; set; } = true;
    public SoundSettings Sound { get; set; } = new();
    public bool RemindOpenDistractionsAtStart { get; set; } = true;
    public double SuggestPlanAfterMinutes { get; set; } = 3;
    public double BreakMinutes { get; set; } = 10;
}

public sealed class SoundSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>SystemExclamation, SystemAsterisk, SystemHand, SystemBeep, or a path to a .wav file.</summary>
    public string File { get; set; } = "SystemExclamation";
    public double RepeatEveryMinutes { get; set; } = 2;
    public int MaxRepeats { get; set; } = 3;
    public bool DistractionsOnly { get; set; }
}

public sealed class SessionSettings
{
    /// <summary>A lock shorter than this pauses the session; longer ends it. Sleep, restart and shutdown always end it.</summary>
    public double EndAfterLockedMinutes { get; set; } = 30;
}

public static class SettingsFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Returns null and an error message when the file exists but can't be read, so callers can keep the previous settings.</summary>
    public static AppSettings? Load(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            error = $"settings.json: {ex.Message}";
            return null;
        }
    }

    public const string Template = """
        {
          // PrepPulse settings. Minutes can be fractions: 0.5 = 30 seconds.
          // Changes are picked up automatically when you save.

          // No keyboard or mouse input for this long is recorded as an idle pause.
          "IdleAfterMinutes": 2,

          "Alerts": {
            // Alert when focus is lost for this long. 0 turns a trigger off.
            "NoInputMinutes": 5,          // no keyboard or mouse input
            "DistractionMinutes": 2,      // something on your distraction list is in focus
            "NeitherListMinutes": 5,      // something on neither list (not in the plan, not a distraction)

            "FlashTrayIcon": true,
            "FlashTaskbar": true,
            "Popup": true,
            "Sound": {
              "Enabled": true,
              "File": "SystemExclamation",   // SystemExclamation, SystemAsterisk, SystemHand, SystemBeep or a path to a .wav
              "RepeatEveryMinutes": 2,
              "MaxRepeats": 3,
              "DistractionsOnly": false      // true = no beep when there's simply no input
            },
            "RemindOpenDistractionsAtStart": true,
            "SuggestPlanAfterMinutes": 3,   // suggest switching plan when you're on another plan's site for this long
            "BreakMinutes": 10
          },

          "Session": {
            // A lock shorter than this pauses the session; longer ends it.
            // Sleep, hibernate, restart and shutdown always end the session.
            "EndAfterLockedMinutes": 30
          },

          "DashboardPort": 5317,
          "DashboardIdleMinutes": 10,     // the dashboard server starts on demand and stops after this long unused (0 = always on)
          "Hotkey": "Ctrl+Alt+P",
          "ShowPickerOnLaunch": true,
          "SessionWindowMinimized": true
        }
        """;
}
