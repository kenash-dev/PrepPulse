using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PrepPulse.Core;

namespace PrepPulse.App;

/// <summary>
/// Where things live. Your files (plans, distraction list, settings) go in Documents\PrepPulse so they're easy to edit;
/// the database and logs go in %LOCALAPPDATA%\PrepPulse. --data-dir puts both under one folder (used for test runs).
/// </summary>
public sealed class AppPaths
{
    private AppPaths(string configDir, string dataDir)
    {
        ConfigDir = configDir;
        DataDir = dataDir;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant()));
        InstanceKey = Convert.ToHexString(hash)[..12];
    }

    public string ConfigDir { get; }
    public string DataDir { get; }
    public string InstanceKey { get; }
    public bool IsDefault { get; private init; }

    public string PlansDir => Path.Combine(ConfigDir, "plans");
    public string DistractionsFile => Path.Combine(ConfigDir, "distractions.yaml");
    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string DatabaseFile => Path.Combine(DataDir, "preppulse.db");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string MutexName => $@"Local\PrepPulse-{InstanceKey}";
    public string PipeName => $"PrepPulse-{InstanceKey}";

    public static AppPaths Resolve(string? dataDirArg, string? configDirArg)
    {
        if (!string.IsNullOrWhiteSpace(dataDirArg))
        {
            var root = Path.GetFullPath(dataDirArg);
            var config = string.IsNullOrWhiteSpace(configDirArg) ? Path.Combine(root, "config") : Path.GetFullPath(configDirArg);
            return new AppPaths(config, Path.Combine(root, "data"));
        }

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var configDir = string.IsNullOrWhiteSpace(configDirArg) ? Path.Combine(docs, "PrepPulse") : Path.GetFullPath(configDirArg);
        return new AppPaths(configDir, Path.Combine(local, "PrepPulse")) { IsDefault = string.IsNullOrWhiteSpace(configDirArg) };
    }

    /// <summary>Creates the folders and, on first run only, copies the example plans and writes default settings.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(PlansDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);

        var marker = Path.Combine(ConfigDir, ".seeded");
        if (!File.Exists(marker))
        {
            var seed = Path.Combine(AppContext.BaseDirectory, "seed", "plans");
            if (Directory.Exists(seed) && !Directory.EnumerateFiles(PlansDir, "*.y*ml").Any())
            {
                foreach (var f in Directory.EnumerateFiles(seed, "*.yaml"))
                    File.Copy(f, Path.Combine(PlansDir, Path.GetFileName(f)), overwrite: false);
            }
            File.WriteAllText(marker, "PrepPulse copied the example plans here once. Delete this file to get them again.\n");
        }

        if (!File.Exists(DistractionsFile)) File.WriteAllText(DistractionsFile, DistractionsTemplate);
        if (!File.Exists(SettingsFile)) File.WriteAllText(SettingsFile, Core.SettingsFile.Template);
        var readme = Path.Combine(PlansDir, "README.txt");
        if (!File.Exists(readme)) File.WriteAllText(readme, PlansReadme);
    }

    private const string DistractionsTemplate = """
        # Distractions — shared by all your study plans.
        # Apps by program name (WhatsApp.exe), websites by domain (reddit.com, which also covers old.reddit.com).
        # Add "| words" to match only when the window title contains those words.
        # A more specific entry wins, so a plan's "youtube.com | system design" beats "youtube.com" here.

        distractions:
          - WhatsApp.exe
          - Discord.exe
          - Telegram.exe
          - reddit.com
          - x.com
          - twitter.com
          - instagram.com
          - facebook.com
          - tiktok.com
          - netflix.com
          - youtube.com
        """;

    private const string PlansReadme = """
        PrepPulse study plans
        =====================

        Each .yaml file in this folder is one study plan. Save a file and PrepPulse picks it up straight away;
        delete or rename it to remove the plan. Problems in a file are shown in the tray menu and the dashboard.

        A plan has:
          name, colour         shown in the tray and dashboard
          start, end           YYYY-MM-DD; leave end out for open-ended study
          targets              weekdays: 2h and weekends: 4h, or weekly: 7h
                               optional days_off: [2026-11-29] and overrides: { 2026-12-13: 6h }
          topics               each with a category (coding, design, ai, mock), hours, optional weeks: 2-5,
                               and the apps/sites that count as studying it:
                                 focus:   [leetcode.com, Code.exe, "youtube.com | system design"]
                                 passive: ["youtube.com | LLM"]   # watching/reading: no input still counts
          weeks                optional week-by-week checklist

        Apps are matched by program name (Code.exe), websites by domain (leetcode.com covers its subdomains).
        "site | words" only matches when the window title contains those words.

        See interview-prep.yaml and learning-ai.yaml for complete examples.
        """;
}
