using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using PrepPulse.App.Interop;

namespace PrepPulse.App.Probes;

public sealed record ForegroundWindow(IntPtr Hwnd, uint Pid, string Process, string Title);

/// <summary>Reads the foreground window and the set of programs with visible windows. Call from one thread only.</summary>
public sealed class WindowProbe
{
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "textinputhost.exe", "shellexperiencehost.exe", "searchhost.exe", "startmenuexperiencehost.exe",
        "applicationframehost.exe", "lockapp.exe", "searchapp.exe", "systemsettingsbroker.exe",
    };

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly Dictionary<uint, (string Name, long At)> _names = new();

    public bool IsOwn(uint pid) => pid == _ownPid;

    public ForegroundWindow? Foreground()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var name = ProcessName(pid);
        if (name == "applicationframehost.exe" && RealUwpProcess(hwnd, pid) is { } child)
        {
            pid = child;
            name = ProcessName(child);
        }
        return new ForegroundWindow(hwnd, pid, name, Native.WindowText(hwnd));
    }

    /// <summary>Lower-case program names that have a normal, visible top-level window right now.</summary>
    public IReadOnlyCollection<string> OpenApps()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Native.EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                if (!IsAppWindow(hwnd)) return true;
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == _ownPid) return true;
                var name = ProcessName(pid);
                if (name == "applicationframehost.exe" && RealUwpProcess(hwnd, pid) is { } child) name = ProcessName(child);
                if (!IgnoredProcesses.Contains(name)) result.Add(name);
            }
            catch (Exception) { /* one odd window shouldn't stop the scan */ }
            return true;
        };
        Native.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    private static bool IsAppWindow(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd)) return false;
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;
        if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (Native.GetWindowTextLengthW(hwnd) == 0) return false;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return !IgnoredClasses.Contains(Native.ClassName(hwnd));
    }

    /// <summary>Store apps run inside ApplicationFrameHost; the real process owns a child window.</summary>
    private static uint? RealUwpProcess(IntPtr frame, uint framePid)
    {
        uint found = 0;
        Native.EnumWindowsProc callback = (child, _) =>
        {
            Native.GetWindowThreadProcessId(child, out var pid);
            if (pid != framePid) { found = pid; return false; }
            return true;
        };
        Native.EnumChildWindows(frame, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found == 0 ? null : found;
    }

    public string ProcessName(uint pid)
    {
        var now = Environment.TickCount64;
        if (_names.TryGetValue(pid, out var cached) && now - cached.At < 60_000) return cached.Name;

        var name = "unknown.exe";
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var size = 1024u;
                var sb = new StringBuilder((int)size);
                if (Native.QueryFullProcessImageNameW(handle, 0, sb, ref size)) name = Path.GetFileName(sb.ToString()).ToLowerInvariant();
            }
            finally { Native.CloseHandle(handle); }
        }
        if (name == "unknown.exe")
        {
            try { using var p = Process.GetProcessById((int)pid); name = (p.ProcessName + ".exe").ToLowerInvariant(); }
            catch (Exception) { /* process already gone */ }
        }

        if (_names.Count > 500) _names.Clear();
        _names[pid] = (name, now);
        return name;
    }
}
