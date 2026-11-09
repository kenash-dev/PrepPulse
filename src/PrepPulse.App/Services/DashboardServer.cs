using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrepPulse.Core;
using PrepPulse.Core.Engine;
using PrepPulse.Data;

namespace PrepPulse.App.Services;

/// <summary>
/// The local dashboard's web server, started only when you open the dashboard and stopped again after
/// <see cref="AppSettings.DashboardIdleMinutes"/> without a request. Most of the day PrepPulse runs with no web
/// server at all. An open dashboard tab refreshes every 30 seconds, which keeps the server alive while you use it.
/// </summary>
public sealed class DashboardServer : IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    private readonly CatalogService _catalog;
    private readonly SqliteStore _store;
    private readonly FocusEngine _engine;
    private readonly IClock _clock;
    private readonly ILoggerProvider _logs;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Timer _idleCheck;
    private WebApplication? _app;
    private long _lastRequestTicks;

    public DashboardServer(CatalogService catalog, SqliteStore store, FocusEngine engine, IClock clock, ILoggerProvider logs, ILogger<DashboardServer> log)
    {
        _catalog = catalog;
        _store = store;
        _engine = engine;
        _clock = clock;
        _logs = logs;
        _log = log;
        _idleCheck = new Timer(_ => StopIfIdle(), null, CheckEvery, CheckEvery);
    }

    /// <summary>The address while the server runs, otherwise null.</summary>
    public string? Url { get; private set; }

    /// <summary>Starts the server if it isn't running and returns its address.</summary>
    public string EnsureStarted()
    {
        lock (_gate)
        {
            Touch();
            if (_app is not null && Url is not null) return Url;

            var port = DashboardApi.ChoosePort(_catalog.Settings.DashboardPort);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(_logs);
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

            var app = builder.Build();
            DashboardApi.Map(app, _catalog, _store, _engine, _clock, Touch);
            // Off the UI thread, so the host's async start can't wait on the UI's synchronisation context.
            Task.Run(() => app.StartAsync()).GetAwaiter().GetResult();

            _app = app;
            Url = $"http://127.0.0.1:{port}/";
            _log.LogInformation("Dashboard started on {Url}", Url);
            return Url;
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);

    private void StopIfIdle()
    {
        var idleMinutes = _catalog.Settings.DashboardIdleMinutes;
        if (idleMinutes <= 0) return;
        var unused = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastRequestTicks), DateTimeKind.Utc);
        if (unused >= TimeSpan.FromMinutes(idleMinutes)) Stop($"unused for {Durations.Spoken(unused)}");
    }

    private void Stop(string why)
    {
        lock (_gate)
        {
            if (_app is not { } app) return;
            _app = null;
            Url = null;
            try
            {
                Task.Run(async () =>
                {
                    await app.StopAsync(TimeSpan.FromSeconds(5));
                    await app.DisposeAsync();
                }).GetAwaiter().GetResult();
                _log.LogInformation("Dashboard stopped ({Why})", why);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Dashboard didn't stop cleanly");
            }
        }
    }

    public void Dispose()
    {
        _idleCheck.Dispose();
        Stop("PrepPulse is exiting");
    }
}
