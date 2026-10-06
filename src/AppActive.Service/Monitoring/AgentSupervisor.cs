using System.Diagnostics;
using AppActive.Core;
using AppActive.Core.Data;
using AppActive.Core.Options;
using AppActive.Core.Services;
using AppActive.Service.Infrastructure;
using AppActive.Service.Windows;

namespace AppActive.Service.Monitoring;

/// <summary>
/// Следит за списком сеансов Windows и держит в каждом активном сеансе процесс-агент:
/// запускает, перезапускает зависшие и завершает вместе со службой.
/// </summary>
internal sealed class AgentSupervisor : BackgroundService
{
    private readonly AppActiveOptions _options;
    private readonly AgentRegistry _registry;
    private readonly SettingsService _settings;
    private readonly ServiceEndpoint _endpoint;
    private readonly ILogger<AgentSupervisor> _logger;
    private string? _agentExecutable;
    private string? _agentExecutableError;

    public AgentSupervisor(AppActiveOptions options, AgentRegistry registry, SettingsService settings,
        ServiceEndpoint endpoint, ILogger<AgentSupervisor> logger)
    {
        _options = options;
        _registry = registry;
        _settings = settings;
        _endpoint = endpoint;
        _logger = logger;
    }

    /// <summary>
    /// Адрес службы для агентов: фактический loopback-адрес Kestrel (порт задан в AppActive:Urls),
    /// а до старта хоста — адрес, выведенный из той же настройки.
    /// </summary>
    public string ServiceUrl => _endpoint.LoopbackUrl ?? _endpoint.FromConfiguration();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _agentExecutable = ResolveAgentExecutable(out _agentExecutableError);
        if (_agentExecutable is null)
        {
            _logger.LogError("Агент не найден: {Error}. Учёт активности в сеансах работать не будет.", _agentExecutableError);
            AppLog.Warn($"Агент сеанса не найден: {_agentExecutableError}");
        }
        else
        {
            AppLog.Info($"Агент сеанса: {_agentExecutable}");
            AgentLauncher.PreparePrivileges();
        }

        if (string.IsNullOrWhiteSpace(ServiceUrl))
        {
            AppLog.Error("Не удалось определить адрес службы для агентов — проверьте AppActive:Urls в appsettings.json");
            return;
        }

        var scanInterval = TimeSpan.FromSeconds(Math.Clamp(_options.Monitoring.SessionScanSeconds, 3, 300));
        AppLog.Info($"Супервизор сеансов запущен, скан каждые {scanInterval.TotalSeconds:0} с, служба для агентов: {ServiceUrl}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Scan();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка сканирования сеансов");
            }

            try
            {
                await Task.Delay(scanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        StopAllAgents();
    }

    private void Scan()
    {
        var sessions = WtsInterop.Enumerate();
        var interactive = sessions
            .Where(s => WtsInterop.IsInteractive(s.State) && !string.IsNullOrWhiteSpace(s.UserName))
            .ToList();

        if (_logger.IsEnabled(LogLevel.Debug))        {
            _logger.LogDebug("Сеансы: {Sessions}", string.Join(", ",
                sessions.Select(s => $"{s.SessionId}:{s.StateName}:{s.DisplayUser}")));
        }

        foreach (var session in interactive)
        {
            var stale = TimeSpan.FromSeconds(Math.Clamp(_options.Monitoring.AgentStaleSeconds, 30, 3600));
            if (_registry.IsAlive(session.SessionId, stale)) continue;
            if (!_options.Monitoring.LaunchAgents) continue;
            if (_agentExecutable is null) continue;

            if (_registry.TryGet(session.SessionId, out var previous) && previous.ProcessId > 0)
            {
                KillIfAgent(previous.ProcessId, session.SessionId);
            }

            Launch(session);
        }

        foreach (var state in _registry.Snapshot())
        {
            if (interactive.Any(s => s.SessionId == state.SessionId)) continue;
            if (state.ProcessId > 0) KillIfAgent(state.ProcessId, state.SessionId);
            _registry.Remove(state.SessionId);
            AppLog.Info($"Сеанс {state.SessionId} больше не активен — агент снят с учёта");
        }
    }

    private void Launch(WtsSession session)
    {
        var settings = _settings.Current;
        try
        {
            var configPath = AgentLauncher.WriteAgentConfig(
                _options.AgentDirectory,
                session.SessionId,
                ServiceUrl,
                _options.Security.IngestKey,
                settings.PollIntervalSeconds,
                settings.ActivityIdleSeconds,
                settings.CollectWindowTitles,
                settings.MonitorEnabled,
                settings.AgentHeartbeatSeconds);

            var result = AgentLauncher.Launch(new AgentLaunchRequest
            {
                SessionId = session.SessionId,
                ExecutablePath = _agentExecutable!,
                ConfigPath = configPath,
                AllowSelfSessionFallback = _options.Monitoring.AllowSelfSessionFallback
            });

            if (result.Success)
            {
                _registry.RegisterSpawned(session.SessionId, result.ProcessId, session.UserName, session.Domain, result.Method);
                AppLog.Info($"Агент запущен в сеансе {session.SessionId} ({session.DisplayUser}), PID={result.ProcessId}, способ: {result.Method}");
            }
            else
            {
                _registry.RegisterFailure(session.SessionId, session.UserName, session.Domain, result.Error);
                AppLog.Warn($"Не удалось запустить агента в сеансе {session.SessionId} ({session.DisplayUser}): {result.Error}");
            }
        }
        catch (Exception ex)
        {
            _registry.RegisterFailure(session.SessionId, session.UserName, session.Domain, ex.Message);
            _logger.LogError(ex, "Ошибка запуска агента в сеансе {SessionId}", session.SessionId);
        }
    }

    private void KillIfAgent(int processId, int sessionId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.ProcessName.StartsWith("AppActive.Agent", StringComparison.OrdinalIgnoreCase)) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            AppLog.Info($"Агент сеанса {sessionId} остановлен (PID={processId})");
        }
        catch (ArgumentException)
        {
            // процесс уже завершился
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Не удалось остановить агент PID={Pid}: {Error}", processId, ex.Message);
        }
    }

    private void StopAllAgents()
    {
        foreach (var state in _registry.Snapshot())
        {
            if (state.ProcessId > 0) KillIfAgent(state.ProcessId, state.SessionId);
        }
    }

    private string? ResolveAgentExecutable(out string? error)
    {
        error = null;
        var configured = _options.Monitoring.AgentExecutable;
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "AppActive.Agent.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "agent", "AppActive.Agent.exe"));
        candidates.Add(Path.Combine(_options.AgentDirectory, "AppActive.Agent.exe"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        var dll = Path.Combine(AppContext.BaseDirectory, "agent", "AppActive.Agent.dll");
        error = File.Exists(dll)
            ? $"найден только {dll}; для запуска в сеансах нужен AppActive.Agent.exe (соберите/опубликуйте проект AppActive.Agent)"
            : $"проверены пути: {string.Join("; ", candidates)}";
        return null;
    }
}
