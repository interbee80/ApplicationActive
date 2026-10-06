using System.Text;
using Microsoft.Extensions.Logging;

namespace AppActive.Service.Infrastructure;

internal sealed class FileLoggerOptions
{
    public string Directory { get; set; } = "";
    public string FilePrefix { get; set; } = "appactive";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public int RetentionDays { get; set; } = 14;
    public bool WriteToConsole { get; set; } = true;
    public string? EventLogSource { get; set; }
}

/// <summary>Простой файловый журнал с суточной нарезкой, ограничением размера и очисткой старых файлов.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 20 * 1024 * 1024;

    private readonly FileLoggerOptions _options;
    private readonly object _gate = new();

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options;
        try
        {
            System.IO.Directory.CreateDirectory(_options.Directory);
        }
        catch
        {
            // журнал не критичен
        }
        PurgeOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        // ресурсы не удерживаются
    }

    internal void Write(string category, LogLevel level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelName(level)}] {category}: {message}";

        lock (_gate)
        {
            if (_options.WriteToConsole)
            {
                Console.WriteLine(line);
            }

            try
            {
                var path = System.IO.Path.Combine(_options.Directory, $"{_options.FilePrefix}-{DateTime.Now:yyyyMMdd}.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    var rolled = path + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(path, rolled);
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // если писать некуда — просто теряем строку журнала
            }
        }

        if (level >= LogLevel.Warning && _options.EventLogSource is not null)
        {
            if (level >= LogLevel.Error) WindowsEventLog.Error(_options.EventLogSource, $"{category}: {message}");
            else WindowsEventLog.Warning(_options.EventLogSource, $"{category}: {message}");
        }
    }

    private void PurgeOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, _options.RetentionDays));
            foreach (var file in System.IO.Directory.GetFiles(_options.Directory, _options.FilePrefix + "-*.log*"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // очистка не критична
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "INFO "
    };
}

internal sealed class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _category;

    public FileLogger(FileLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null) message = $"{message} | {exception.GetType().Name}: {exception.Message}";
        _provider.Write(_category, logLevel, message);
    }
}
