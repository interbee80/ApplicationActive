using System.Diagnostics;
using System.Text.Json;

namespace AppActive.Agent;

/// <summary>Конфигурация агента: читается из файла, который готовит служба, и обновляется ответами ingest.</summary>
internal sealed class AgentConfig
{
    public string ServiceUrl { get; set; } = "http://127.0.0.1:5090";
    public string ApiKey { get; set; } = "";
    public int SessionId { get; set; }
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 5;
    public int ActivityIdleSeconds { get; set; } = 60;
    public bool CollectWindowTitles { get; set; } = true;
    public bool MonitorEnabled { get; set; } = true;
    public int HeartbeatSeconds { get; set; } = 60;
    public string? LogFile { get; set; }
    public string LogLevel { get; set; } = "info";

    public static AgentConfig Load(string? path, int sessionIdOverride)
    {
        var config = new AgentConfig();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<AgentConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) ?? new AgentConfig();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Не удалось прочитать конфигурацию '{path}': {ex.Message}");
                config = new AgentConfig();
            }
        }

        if (sessionIdOverride > 0) config.SessionId = sessionIdOverride;
        if (config.SessionId <= 0) config.SessionId = Process.GetCurrentProcess().SessionId;
        if (string.IsNullOrWhiteSpace(config.UserName)) config.UserName = Environment.UserName;
        if (string.IsNullOrWhiteSpace(config.Domain)) config.Domain = Environment.UserDomainName;
        if (string.IsNullOrWhiteSpace(config.LogFile))
        {
            // Журнал агента пишем в профиль пользователя, а если он недоступен — во временный каталог.
            config.LogFile = ResolveLogFile(config.SessionId);
        }
        return config;
    }

    /// <summary>Подбирает доступный для записи каталог журнала агента.</summary>
    private static string? ResolveLogFile(int sessionId)
    {
        var fileName = $"agent-session-{sessionId}.log";
        var candidates = new List<string>();
        try
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppActive"));
        }
        catch
        {
            // профиль пользователя недоступен
        }
        try
        {
            candidates.Add(Path.Combine(Path.GetTempPath(), "AppActive"));
        }
        catch
        {
            // временный каталог недоступен
        }

        foreach (var directory in candidates)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return Path.Combine(directory, fileName);
            }
            catch
            {
                // пробуем следующий каталог
            }
        }

        return null;
    }

    public void ApplyFrom(AgentConfigDto dto)
    {
        var changed = false;
        if (dto.PollIntervalSeconds is >= 1 and <= 3600 && dto.PollIntervalSeconds != PollIntervalSeconds)
        {
            PollIntervalSeconds = dto.PollIntervalSeconds;
            changed = true;
        }
        if (dto.ActivityIdleSeconds is >= 1 and <= 86400) ActivityIdleSeconds = dto.ActivityIdleSeconds;
        if (dto.HeartbeatSeconds is >= 10 and <= 3600) HeartbeatSeconds = dto.HeartbeatSeconds;
        CollectWindowTitles = dto.CollectWindowTitles;
        MonitorEnabled = dto.MonitorEnabled;
        if (changed) Changed?.Invoke();
    }

    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();
}

internal sealed class AgentConfigDto
{
    public int PollIntervalSeconds { get; set; }
    public int ActivityIdleSeconds { get; set; }
    public bool MonitorEnabled { get; set; } = true;
    public bool CollectWindowTitles { get; set; } = true;
    public int HeartbeatSeconds { get; set; } = 60;
}

internal sealed class AgentSampleDto
{
    public string AppKey { get; set; } = "";
    public string ExeName { get; set; } = "";
    public string? ExePath { get; set; }
    public string? WindowTitle { get; set; }
    public DateTime StartUtc { get; set; }
    public double Seconds { get; set; }
}

internal sealed class AgentReportRequest
{
    public int SessionId { get; set; }
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public int Pid { get; set; }
    public string AgentVersion { get; set; } = "";
    public double CpuPercent { get; set; }
    public double WorkingSetMb { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<AgentSampleDto> Samples { get; set; } = new();
}

internal sealed class AgentReportResponse
{
    public bool Ok { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public AgentConfigDto? Config { get; set; }
}

/// <summary>Аргументы командной строки агента.</summary>
internal sealed class AgentArgs
{
    public string? ConfigPath { get; private set; }
    public int SessionId { get; private set; }
    public bool RunOnce { get; private set; }
    public bool SelfTest { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool Verbose { get; private set; }

    public static AgentArgs Parse(string[] args)
    {
        var result = new AgentArgs();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--config":
                case "-c":
                    if (i + 1 < args.Length) result.ConfigPath = args[++i];
                    break;
                case "--session":
                case "-s":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var session)) result.SessionId = session;
                    break;
                case "--once":
                    result.RunOnce = true;
                    break;
                case "--self-test":
                    result.SelfTest = true;
                    break;
                case "--verbose":
                case "-v":
                    result.Verbose = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    result.ShowHelp = true;
                    break;
            }
        }
        return result;
    }
}

/// <summary>Простое журналирование агента в файл (с ограничением размера).</summary>
internal static class AgentLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _verbose;
    private static int _sessionId;

    public static void Init(string? path, string level, int sessionId)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _verbose = level.Equals("debug", StringComparison.OrdinalIgnoreCase) || level.Equals("trace", StringComparison.OrdinalIgnoreCase);
        _sessionId = sessionId;
        if (_path is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            }
            catch
            {
                _path = null;
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Debug(string message)
    {
        if (_verbose) Write("DEBUG", message);
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] session={_sessionId} {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            if (_path is null) return;
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // Журналирование не критично для учёта.
            }
        }
    }
}
