using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrepPulse.App.Interop;
using PrepPulse.App.Probes;
using PrepPulse.App.Services;
using PrepPulse.App.Ui;
using PrepPulse.Core;
using PrepPulse.Core.Engine;
using PrepPulse.Core.Matching;
using PrepPulse.Data;

namespace PrepPulse.App;

/// <summary>
/// Entry point. Composition root: builds the host (DI, background services, local dashboard), then runs the WPF
/// message loop for the tray, windows and hotkey. A second PrepPulse.exe forwards its command to the first and exits.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [STAThread]
    public static int Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.Error is not null)
        {
            WriteOut(options.Out, options.Error);
            return 2;
        }

        var paths = AppPaths.Resolve(options.DataDir, options.ConfigDir);

        // Modes that don't need the running app.
        if (options.Probe is { } seconds) return RunProbe(paths, seconds, options.Out);
        if (options.Report is { } reportFile) return RunReport(paths, reportFile, options.Plan);

        using var mutex = new Mutex(true, paths.MutexName, out var firstInstance);
        if (!firstInstance)
        {
            var reply = PipeClient.Send(paths, options.Command ?? "show") ?? "{\"ok\":false,\"message\":\"PrepPulse didn't answer.\"}";
            WriteOut(options.Out, reply);
            return 0;
        }

        if (options.Command is "stop" or "status" or "pause" or "resume" or "break" or "exit" or "dashboard")
        {
            WriteOut(options.Out, "{\"ok\":false,\"message\":\"PrepPulse isn't running.\"}");
            return 1;
        }

        paths.EnsureCreated();
        var logProvider = new FileLoggerProvider(paths.LogsDir);
        var bootLog = logProvider.CreateLogger("PrepPulse.App.Program");
        AppLog.Init(bootLog);
        bootLog.LogInformation("PrepPulse {Version} starting. Config {Config}, data {Data}",
            typeof(Program).Assembly.GetName().Version, paths.ConfigDir, paths.DataDir);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => bootLog.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");

        try
        {
            return Run(options, paths, logProvider, bootLog);
        }
        catch (Exception ex)
        {
            bootLog.LogCritical(ex, "PrepPulse failed to start");
            MessageBox.Show($"PrepPulse couldn't start:\n\n{ex.Message}\n\nDetails are in {logProvider.CurrentFile}", "PrepPulse",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static int Run(CliOptions options, AppPaths paths, FileLoggerProvider logProvider, ILogger log)
    {
        var clock = new SystemClock();
        var store = new SqliteStore(paths.DatabaseFile);
        var recovered = store.CloseUnfinishedSessions();
        if (recovered > 0) log.LogWarning("Closed {Count} session(s) left open by a crash or power loss", recovered);

        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logProvider));
        var catalog = new CatalogService(paths, loggerFactory.CreateLogger<CatalogService>());
        catalog.Load();
        catalog.Watch();

        var engine = new FocusEngine(clock, store, () => catalog.Settings, () => catalog.Catalog);
        engine.Event += e => log.LogInformation("Event: {Event}", e);

        var port = DashboardApi.ChoosePort(catalog.Settings.DashboardPort);
        var dashboardUrl = $"http://127.0.0.1:{port}/";

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logProvider);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(engine);
        builder.Services.AddSingleton<WindowProbe>();
        builder.Services.AddSingleton<BrowserAddressProbe>();
        builder.Services.AddSingleton<CommandRouter>();
        builder.Services.AddHostedService<SamplerService>();
        builder.Services.AddHostedService<PipeServer>();

        var app = builder.Build();
        DashboardApi.Map(app, catalog, store, engine, clock);
        app.StartAsync().GetAwaiter().GetResult();
        log.LogInformation("Dashboard on {Url}", dashboardUrl);

        var router = app.Services.GetRequiredService<CommandRouter>();
        router.DashboardUrl = dashboardUrl;

        var wpf = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        wpf.DispatcherUnhandledException += (_, e) =>
        {
            log.LogError(e.Exception, "UI error");
            e.Handled = true;
        };
        System.Windows.Forms.Application.EnableVisualStyles();

        var shell = new Shell(engine, catalog, store, paths, clock, dashboardUrl, recovered);
        wpf.SessionEnding += (_, _) => shell.OnWindowsShutdown();
        wpf.Startup += (_, _) =>
        {
            router.AttachShell(shell);
            shell.Start(options.Command, router);
        };

        var code = wpf.Run();

        log.LogInformation("Shutting down");
        engine.StopSession(EndReason.AppExit);
        shell.Dispose();
        catalog.Dispose();
        app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return code;
    }

    /// <summary>--probe: logs what PrepPulse sees (foreground app, title, browser domain, idle time) once a second.</summary>
    private static int RunProbe(AppPaths paths, int seconds, string? outFile)
    {
        Directory.CreateDirectory(paths.LogsDir);
        var file = outFile ?? Path.Combine(paths.LogsDir, "probe.txt");
        var windows = new WindowProbe();
        var browser = new BrowserAddressProbe();
        var sb = new StringBuilder();
        sb.AppendLine($"PrepPulse probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {seconds}s");
        for (var i = 0; i < seconds; i++)
        {
            var fg = windows.Foreground();
            string line;
            if (fg is null) line = "(no foreground window)";
            else
            {
                var isBrowser = Browsers.Is(fg.Process);
                var isPrivate = isBrowser && Browsers.IsPrivateTitle(fg.Title);
                var domain = isBrowser && !isPrivate ? browser.Domain(fg.Hwnd, fg.Process, fg.Title) : null;
                line = $"{fg.Process} | domain={domain ?? "-"}{(isPrivate ? " (private)" : "")} | title={fg.Title}";
            }
            sb.AppendLine($"{DateTime.Now:HH:mm:ss} idle={Native.IdleFor().TotalSeconds,5:0}s | {line}");
            if (i == 0 || i == seconds - 1) sb.AppendLine($"           open apps: {string.Join(", ", windows.OpenApps().OrderBy(x => x))}");
            File.WriteAllText(file, sb.ToString());
            Thread.Sleep(1000);
        }
        return 0;
    }

    /// <summary>--report file.json [--plan slug]: writes the dashboard data from the database, without the UI.</summary>
    private static int RunReport(AppPaths paths, string file, string? plan)
    {
        var store = new SqliteStore(paths.DatabaseFile);
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var catalog = new CatalogService(paths, loggerFactory.CreateLogger<CatalogService>());
        catalog.Load();
        var model = DashboardApi.Build(catalog, store, null, new SystemClock(), plan);
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(file, JsonSerializer.Serialize(model, Json));
        return 0;
    }

    private static void WriteOut(string? file, string text)
    {
        if (file is null) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(file, text + Environment.NewLine);
    }
}

/// <summary>
/// PrepPulse.exe [--data-dir DIR] [--config-dir DIR] [--start PLAN | --stop | --pause | --resume | --break | --status |
/// --dashboard | --show | --exit] [--out FILE] [--probe SECONDS] [--report FILE [--plan SLUG]]
/// </summary>
public sealed class CliOptions
{
    public string? DataDir { get; private set; }
    public string? ConfigDir { get; private set; }
    public string? Command { get; private set; }
    public string? Out { get; private set; }
    public int? Probe { get; private set; }
    public string? Report { get; private set; }
    public string? Plan { get; private set; }
    public string? Error { get; private set; }

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i].ToLowerInvariant();
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a)
            {
                case "--data-dir": o.DataDir = Next(); break;
                case "--config-dir": o.ConfigDir = Next(); break;
                case "--out": o.Out = Next(); break;
                case "--plan": o.Plan = Next(); break;
                case "--report": o.Report = Next() ?? "report.json"; break;
                case "--probe":
                    o.Probe = i + 1 < args.Count && int.TryParse(args[i + 1], out var s) ? s : 20;
                    if (i + 1 < args.Count && int.TryParse(args[i + 1], out _)) i++;
                    break;
                case "--start":
                    var plan = Next();
                    if (plan is null) o.Error = "--start needs a plan, e.g. --start interview-prep";
                    else o.Command = $"start {plan}";
                    break;
                case "--stop" or "--pause" or "--resume" or "--break" or "--status" or "--dashboard" or "--show" or "--exit":
                    o.Command = a[2..];
                    break;
                case "--background":
                    break;
                default:
                    o.Error = $"Unknown option '{args[i]}'.";
                    break;
            }
        }
        return o;
    }
}
