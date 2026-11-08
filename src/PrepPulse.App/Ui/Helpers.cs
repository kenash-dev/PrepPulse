using System;
using System.IO;
using System.Media;
using System.Windows.Interop;
using Microsoft.Win32;
using PrepPulse.App.Interop;

namespace PrepPulse.App.Ui;

/// <summary>Plays the alert sound: a Windows system sound by name, or a .wav file.</summary>
internal static class Sounds
{
    private static SoundPlayer? _player;
    private static string? _playerFile;

    public static void Play(string spec)
    {
        try
        {
            switch (spec.Trim().ToLowerInvariant())
            {
                case "systemexclamation": SystemSounds.Exclamation.Play(); return;
                case "systemasterisk": SystemSounds.Asterisk.Play(); return;
                case "systemhand": SystemSounds.Hand.Play(); return;
                case "systemquestion": SystemSounds.Question.Play(); return;
                case "systembeep": SystemSounds.Beep.Play(); return;
            }
            if (File.Exists(spec))
            {
                if (_player is null || _playerFile != spec)
                {
                    _player?.Dispose();
                    _player = new SoundPlayer(spec);
                    _playerFile = spec;
                }
                _player.Play();
                return;
            }
            SystemSounds.Exclamation.Play();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Couldn't play sound '{spec}'", ex);
        }
    }
}

/// <summary>A global hotkey (default Ctrl+Alt+P) on a message-only window.</summary>
internal sealed class Hotkey : IDisposable
{
    private const int Id = 0x5050;
    private readonly HwndSource? _source;
    private readonly Action _onPress;

    public Hotkey(string spec, Action onPress)
    {
        _onPress = onPress;
        if (!TryParse(spec, out var modifiers, out var vk))
        {
            AppLog.Warn($"Hotkey '{spec}' isn't valid; use e.g. Ctrl+Alt+P.");
            return;
        }
        _source = new HwndSource(new HwndSourceParameters("PrepPulseHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _source.AddHook(WndProc);
        Registered = Native.RegisterHotKey(_source.Handle, Id, modifiers | Native.MOD_NOREPEAT, vk);
        if (!Registered) AppLog.Warn($"Hotkey {spec} is already used by another app.");
    }

    public bool Registered { get; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            _onPress();
        }
        return IntPtr.Zero;
    }

    public static bool TryParse(string spec, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": modifiers |= Native.MOD_CONTROL; break;
                case "ALT": modifiers |= Native.MOD_ALT; break;
                case "SHIFT": modifiers |= Native.MOD_SHIFT; break;
                case "WIN" or "WINDOWS": modifiers |= Native.MOD_WIN; break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0])) vk = part[0];
                    else if (part.Length is 2 or 3 && part[0] == 'F' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24) vk = (uint)(0x6F + f);
                    else return false;
                    break;
            }
        }
        return vk != 0 && modifiers != 0;
    }

    public void Dispose()
    {
        if (_source is null) return;
        Native.UnregisterHotKey(_source.Handle, Id);
        _source.Dispose();
    }
}

/// <summary>"Start with Windows" via the per-user Run key. No admin rights needed.</summary>
internal static class StartWithWindows
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PrepPulse";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(Name) is string;
        }
        catch (Exception) { return false; }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (on) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
