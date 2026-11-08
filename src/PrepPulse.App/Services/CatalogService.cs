using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using PrepPulse.Core;
using PrepPulse.Core.Plans;

namespace PrepPulse.App.Services;

/// <summary>
/// Holds the current plans, distraction list and settings, and reloads them when anything in the config folder changes
/// (debounced). Readers get an immutable snapshot, so a reload never changes data under a running tick.
/// </summary>
public sealed class CatalogService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly ILogger<CatalogService> _log;
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private volatile PlanCatalogSnapshot _catalog = PlanCatalogSnapshot.Empty;
    private volatile AppSettings _settings = new();

    public CatalogService(AppPaths paths, ILogger<CatalogService> log)
    {
        _paths = paths;
        _log = log;
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action? Changed;

    public PlanCatalogSnapshot Catalog => _catalog;
    public AppSettings Settings => _settings;
    public string? SettingsError { get; private set; }

    public IReadOnlyList<string> Problems =>
        _catalog.Errors.Select(e => $"{e.File}: {e.Message}")
            .Concat(SettingsError is { } err ? new[] { err } : Array.Empty<string>())
            .ToList();

    public void Load()
    {
        var catalog = PlanLoader.LoadFolder(_paths.PlansDir, _paths.DistractionsFile);
        var settings = SettingsFile.Load(_paths.SettingsFile, out var error);
        if (settings is not null) _settings = settings;
        SettingsError = error;
        _catalog = catalog;

        _log.LogInformation("Loaded {Plans} plan(s) [{Names}], {Distractions} distraction(s), {Errors} problem(s)",
            catalog.Plans.Count, string.Join(", ", catalog.Plans.Select(p => p.Slug)), catalog.Distractions.Count, Problems.Count);
        foreach (var p in Problems) _log.LogWarning("Config problem: {Problem}", p);
    }

    public void Watch()
    {
        _watcher = new FileSystemWatcher(_paths.ConfigDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (ext is ".yaml" or ".yml" or ".json") _debounce.Change(600, Timeout.Infinite);
    }

    public void Reload()
    {
        try
        {
            Load();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reloading plans failed");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
