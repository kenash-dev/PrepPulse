using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrepPulse.App.Interop;
using PrepPulse.App.Probes;
using PrepPulse.Core;
using PrepPulse.Core.Engine;
using PrepPulse.Core.Matching;

namespace PrepPulse.App.Services;

/// <summary>
/// Once a second, while a session runs: read the foreground window, the browser domain and the idle time, and feed
/// the engine. Every 30 seconds also list the programs with open windows.
/// </summary>
public sealed class SamplerService(FocusEngine engine, IClock clock, WindowProbe windows, BrowserAddressProbe browser, ILogger<SamplerService> log)
    : BackgroundService
{
    private static readonly TimeSpan AppsEvery = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastApps = DateTimeOffset.MinValue;
    private long? _appsSession;
    private int _failures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    TickOnce();
                    _failures = 0;
                }
                catch (Exception ex)
                {
                    if (_failures++ < 5) log.LogError(ex, "Sampling failed");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void TickOnce()
    {
        var snap = engine.Snapshot;
        if (!snap.IsRunning)
        {
            _appsSession = null;
            return;
        }

        var now = clock.Now;
        var fg = windows.Foreground();
        Observation? obs = null;
        var own = false;
        if (fg is not null)
        {
            own = windows.IsOwn(fg.Pid);
            if (!own)
            {
                var isBrowser = Browsers.Is(fg.Process);
                var isPrivate = isBrowser && Browsers.IsPrivateTitle(fg.Title);
                var domain = isBrowser && !isPrivate ? browser.Domain(fg.Hwnd, fg.Process, fg.Title) : null;
                obs = new Observation(fg.Process, fg.Title, domain, isBrowser, isPrivate);
            }
        }

        engine.Tick(new Sample(now, obs, Native.IdleFor(), own));

        if (snap.SessionId != _appsSession || now - _lastApps >= AppsEvery)
        {
            _appsSession = snap.SessionId;
            _lastApps = now;
            engine.OnOpenWindows(windows.OpenApps());
        }
    }
}
