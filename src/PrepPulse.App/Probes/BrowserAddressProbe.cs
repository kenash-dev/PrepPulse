using System;
using System.Threading.Tasks;
using System.Windows.Automation;
using PrepPulse.Core.Matching;

namespace PrepPulse.App.Probes;

/// <summary>
/// Reads the domain from a browser's address bar with UI Automation (no browser extension needed).
/// Only the domain is kept. Lookups run off-thread with a time limit and are cached per window + title,
/// so they happen when you switch tab or window, not every second.
/// </summary>
public sealed class BrowserAddressProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(800);

    private IntPtr _hwnd;
    private string? _title;
    private string? _domain;
    private bool _cached;

    private Task<string?>? _pending;
    private IntPtr _pendingHwnd;
    private string? _pendingTitle;

    public string? Domain(IntPtr hwnd, string process, string title)
    {
        if (_cached && hwnd == _hwnd && title == _title) return _domain;

        if (_pending is not null)
        {
            var sameKey = _pendingHwnd == hwnd && _pendingTitle == title;
            if (!_pending.IsCompleted)
            {
                if (!sameKey) return null; // previous lookup still running; try again next second
                if (!_pending.Wait(Wait)) return null;
            }
            var result = _pending.IsCompletedSuccessfully ? _pending.Result : null;
            var (h, t) = (_pendingHwnd, _pendingTitle);
            _pending = null;
            Remember(h, t, result);
            if (sameKey) return result;
        }

        _pendingHwnd = hwnd;
        _pendingTitle = title;
        _pending = Task.Run(() => Read(hwnd, process));
        if (!_pending.Wait(Wait)) return null;
        var value = _pending.IsCompletedSuccessfully ? _pending.Result : null;
        _pending = null;
        Remember(hwnd, title, value);
        return value;
    }

    private void Remember(IntPtr hwnd, string? title, string? domain)
    {
        _hwnd = hwnd;
        _title = title;
        _domain = domain;
        _cached = true;
    }

    private static string? Read(IntPtr hwnd, string process)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            AutomationElement? edit = null;
            if (process.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase))
                edit = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"));

            // Chromium browsers (Chrome, Edge, Brave...): the omnibox is named "Address and search bar" in English.
            edit ??= root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.NameProperty, "Address and search bar")));

            // Other languages: the first edit box in the window is the address bar.
            edit ??= root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edit is null) return null;

            if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value)
                return DomainExtractor.FromAddress(value.Current.Value);
            return null;
        }
        catch (Exception)
        {
            return null; // window closed, browser busy, or UIA unavailable — fall back to the window title
        }
    }
}
