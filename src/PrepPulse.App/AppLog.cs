using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PrepPulse.App;

/// <summary>Plain daily log files in the data folder's logs directory. No logging package needed.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly object _gate = new();

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public string CurrentFile => Path.Combine(_dir, $"preppulse-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_gate)
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine); }
            catch (IOException) { /* never let logging take the app down */ }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        private readonly string _short = category[(category.LastIndexOf('.') + 1)..];
        private readonly bool _framework = category.StartsWith("Microsoft", StringComparison.Ordinal) || category.StartsWith("System", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= (_framework ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "DBG",
            };
            var text = $"{DateTime.Now:HH:mm:ss.fff} {level} {_short}: {formatter(state, exception)}";
            if (exception is not null) text += Environment.NewLine + exception;
            owner.Write(text);
        }
    }
}

/// <summary>Static access for UI code that isn't created through DI.</summary>
public static class AppLog
{
    private static ILogger? _log;

    public static void Init(ILogger log) => _log = log;

    public static void Info(string message) => _log?.LogInformation("{Message}", message);
    public static void Warn(string message, Exception? ex = null) => _log?.LogWarning(ex, "{Message}", message);
    public static void Error(string message, Exception? ex = null) => _log?.LogError(ex, "{Message}", message);
}
