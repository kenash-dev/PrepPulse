using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrepPulse.Core;
using PrepPulse.Core.Engine;

namespace PrepPulse.App.Services;

/// <summary>Things the running app can be asked to do, by the tray, the hotkey, or another PrepPulse.exe on the command line.</summary>
public interface IShellCommands
{
    void Show();
    void OpenDashboard();
    void Exit();
}

/// <summary>
/// Text commands ("start interview-prep", "stop", "status"...) from the command line, routed to the engine or the UI.
/// Single-instance: a second PrepPulse.exe forwards its arguments here over a per-user named pipe and exits.
/// </summary>
public sealed class CommandRouter(FocusEngine engine, CatalogService catalog, DashboardServer dashboard, ILogger<CommandRouter> log)
{
    private readonly TaskCompletionSource<IShellCommands> _shell = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void AttachShell(IShellCommands shell) => _shell.TrySetResult(shell);

    public string Handle(string line)
    {
        var text = line.Trim();
        var space = text.IndexOf(' ');
        var verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var arg = space < 0 ? null : text[(space + 1)..].Trim();
        log.LogInformation("Command: {Command}", text);

        try
        {
            switch (verb)
            {
                case "start":
                    if (string.IsNullOrWhiteSpace(arg)) return Reply(false, "Say which plan, e.g. start interview-prep.");
                    var plan = catalog.Catalog.Find(arg) ?? FindByName(arg);
                    if (plan is null) return Reply(false, $"No plan '{arg}'. Plans: {string.Join(", ", PlanSlugs())}");
                    if (engine.Snapshot.IsRunning) engine.SwitchPlan(plan.Slug);
                    else engine.StartSession(plan.Slug);
                    return Status();
                case "stop": engine.StopSession(); return Status();
                case "pause": engine.Pause(); return Status();
                case "resume": engine.Resume(); return Status();
                case "break": engine.StartBreak(); return Status();
                case "status": return Status();
                case "show": WithShell(s => s.Show()); return Reply(true, "shown");
                case "dashboard": WithShell(s => s.OpenDashboard()); return Reply(true, dashboard.EnsureStarted());
                case "exit": WithShell(s => s.Exit()); return Reply(true, "exiting");
                default: return Reply(false, $"Unknown command '{verb}'.");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Command failed: {Command}", text);
            return Reply(false, ex.Message);
        }
    }

    private Core.Plans.Plan? FindByName(string name)
    {
        foreach (var p in catalog.Catalog.Plans)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    private string[] PlanSlugs()
    {
        var plans = catalog.Catalog.Plans;
        var slugs = new string[plans.Count];
        for (var i = 0; i < plans.Count; i++) slugs[i] = plans[i].Slug;
        return slugs;
    }

    private void WithShell(Action<IShellCommands> action)
    {
        if (!_shell.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The app is still starting.");
        action(_shell.Task.Result);
    }

    private string Status()
    {
        var s = engine.Snapshot;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            session = s.Session.ToString(),
            focus = s.Focus.ToString(),
            plan = s.PlanSlug,
            current = s.Current,
            list = s.CurrentList?.ToString(),
            sessionMinutes = Math.Round(s.SessionLength.TotalMinutes, 1),
            prepTodayMinutes = Math.Round(s.PrepToday.TotalMinutes, 1),
            targetTodayMinutes = Math.Round(s.TargetToday.TotalMinutes, 1),
            alert = s.Alert?.Trigger.ToString(),
            dashboard = dashboard.Url ?? "not running (starts when you open the dashboard)",
        }, Json);
    }

    private static string Reply(bool ok, string message) => JsonSerializer.Serialize(new { ok, message }, Json);
}

public sealed class PipeServer(AppPaths paths, CommandRouter router, ILogger<PipeServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(paths.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(server, leaveOpen: true);
                await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(stoppingToken);
                var reply = line is null ? "{}" : await Task.Run(() => router.Handle(line), stoppingToken);
                await writer.WriteLineAsync(reply);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Command pipe error");
                await Task.Delay(250, CancellationToken.None);
            }
        }
    }
}

public static class PipeClient
{
    /// <summary>Sends one command to the running instance and returns its reply, or null if none is running.</summary>
    public static string? Send(AppPaths paths, string command, int timeoutMs = 5000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", paths.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            writer.WriteLine(command);
            return reader.ReadLine();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
