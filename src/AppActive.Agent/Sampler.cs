using System.Collections.Concurrent;
using System.Diagnostics;

namespace AppActive.Agent;

/// <summary>
/// Тактирование учёта: раз в интервал опроса вычисляет, сколько секунд за прошедший интервал
/// приложение было активным, и передаёт накопленное в очередь отправки.
/// Интервал опроса настраивается из Web-панели и применяется «на лету».
/// </summary>
internal sealed class Sampler : IDisposable
{
    private readonly AgentConfig _config;
    private readonly ForegroundWatcher _watcher;
    private readonly Reporter _reporter;
    private readonly BlockingCollection<AgentSampleDto[]> _queue = new(new ConcurrentQueue<AgentSampleDto[]>(), 64);
    private readonly object _gate = new();

    private System.Threading.Timer? _timer;
    private Thread? _sender;
    private DateTime _lastTickUtc;
    private DateTime _lastHeartbeatUtc;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuAt;
    private volatile bool _stopping;

    public Sampler(AgentConfig config, ForegroundWatcher watcher, Reporter reporter)
    {
        _config = config;
        _watcher = watcher;
        _reporter = reporter;
        _config.Changed += OnConfigChanged;
    }

    public long Ticks { get; private set; }
    public double ActiveSeconds { get; private set; }
    public long SamplesQueued { get; private set; }
    public string LastStatus { get; private set; } = "инициализация";

    /// <summary>Сколько неудачных отчётов подряд считается поводом завершить агент.</summary>
    public const int MaxConsecutiveFailures = 30;

    /// <summary>Служба недоступна слишком долго — агент должен завершиться (его перезапустит супервизор).</summary>
    public event Action? FatalError;

    public void Start()
    {
        _lastTickUtc = DateTime.UtcNow;
        _lastHeartbeatUtc = DateTime.UtcNow;
        _sender = new Thread(SenderLoop)
        {
            IsBackground = true,
            Name = "appactive-agent-sender"
        };
        _sender.Start();

        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        ScheduleTimer();

        // Сразу сообщаем службе о себе, чтобы она видела живой агент даже в полностью простаивающем сеансе.
        _queue.TryAdd(Array.Empty<AgentSampleDto>());
    }

    private void OnConfigChanged() => ScheduleTimer();

    private void ScheduleTimer()
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _config.PollIntervalSeconds));
        try
        {
            _timer?.Change(period, period);
        }
        catch (ObjectDisposedException)
        {
            // таймер уже остановлен
        }
    }

    /// <summary>Один такт учёта (публично — для диагностического режима --once).</summary>
    public void TickNow()
    {
        _lastTickUtc = DateTime.UtcNow.AddSeconds(-Math.Max(1, _config.PollIntervalSeconds));
        Tick();
    }

    private void Tick()
    {
        if (_stopping) return;
        try
        {
            var now = DateTime.UtcNow;
            var from = _lastTickUtc;
            _lastTickUtc = now;

            var deltaSeconds = (now - from).TotalSeconds;
            if (deltaSeconds < 0.2) return;

            // Сон/гибернация/долгая приостановка: этот интервал не учитываем вовсе.
            var maxDelta = Math.Max(30.0, _config.PollIntervalSeconds * 3.0);
            if (deltaSeconds > maxDelta)
            {
                LastStatus = $"пропуск {deltaSeconds:0} с (сон или приостановка)";
                AgentLog.Warn($"Пропущен интервал {deltaSeconds:0} с — время не учитывается");
                _watcher.CollectSegments(from, now);
                return;
            }

            Ticks++;
            var samples = CollectSamples(from, now, deltaSeconds);

            var heartbeatDue = (now - _lastHeartbeatUtc).TotalSeconds >= Math.Max(10, _config.HeartbeatSeconds);
            if (samples.Count == 0 && !heartbeatDue) return;

            if (samples.Count > 0)
            {
                ActiveSeconds += samples.Sum(s => s.Seconds);
                SamplesQueued += samples.Count;
                _lastHeartbeatUtc = now;
                LastStatus = $"учтено {samples.Sum(s => s.Seconds):0.0} с в {samples.Count} прил.";
            }
            else
            {
                LastStatus = "простой";
            }

            if (!_queue.TryAdd(samples.ToArray()))
            {
                AgentLog.Warn("Очередь отчётов переполнена, отчёт пропущен");
            }
        }
        catch (Exception ex)
        {
            LastStatus = "ошибка: " + ex.Message;
            AgentLog.Error($"Ошибка такта учёта: {ex.Message}");
        }
    }

    private List<AgentSampleDto> CollectSamples(DateTime from, DateTime to, double deltaSeconds)
    {
        var samples = new List<AgentSampleDto>();
        var segments = _watcher.CollectSegments(from, to);
        if (!_config.MonitorEnabled) return samples;

        var idleMs = ForegroundWatcher.GetIdleMilliseconds();

        foreach (var segment in segments)
        {
            var window = segment.Window;
            if (window is null || window.Handle == IntPtr.Zero || string.IsNullOrWhiteSpace(window.ExeName)) continue;
            if (ForegroundWatcher.IsLockScreen(window.ExeName)) continue;

            var seconds = ActivityMath.ComputeCreditSeconds(segment.StartUtc, segment.EndUtc, to, idleMs,
                _config.ActivityIdleSeconds, deltaSeconds);
            if (seconds <= 0) continue;

            var title = _config.CollectWindowTitles ? _watcher.GetTitle(window.Handle) : null;
            var previous = samples.Count > 0 ? samples[^1] : null;
            if (previous is not null && previous.AppKey == window.AppKey)
            {
                previous.Seconds += seconds;
                if (string.IsNullOrEmpty(previous.WindowTitle) && !string.IsNullOrEmpty(title)) previous.WindowTitle = title;
            }
            else
            {
                samples.Add(new AgentSampleDto
                {
                    AppKey = window.AppKey,
                    ExeName = window.ExeName,
                    ExePath = string.IsNullOrWhiteSpace(window.ExePath) ? null : window.ExePath,
                    WindowTitle = string.IsNullOrWhiteSpace(title) ? null : title,
                    StartUtc = segment.EndUtc.AddSeconds(-seconds),
                    Seconds = Math.Round(seconds, 2)
                });
            }
        }

        return samples;
    }

    private void SenderLoop()
    {
        try
        {
            foreach (var batch in _queue.GetConsumingEnumerable())
            {
                if (_stopping && _queue.Count == 0 && batch.Length == 0) break;
                var cpu = CpuPercent();
                var delivered = _reporter.Send(batch, cpu, AgentResource.WorkingSetMb, includeErrors: true);
                if (!delivered && _reporter.ConsecutiveFailures >= MaxConsecutiveFailures)
                {
                    // Служба недоступна слишком долго (например, изменился порт): завершаемся,
                    // чтобы не остаться «осиротевшим» процессом — супервизор запустит агент заново.
                    AgentLog.Error($"Служба недоступна: {_reporter.ConsecutiveFailures} неудачных отчётов подряд " +
                                   $"({_reporter.LastError}). Агент завершает работу.");
                    FatalError?.Invoke();
                    return;
                }
                if (batch.Length == 0)
                {
                    AgentLog.Debug("Отправлен heartbeat");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // штатное завершение
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Поток отправки остановлен: {ex.Message}");
        }
    }

    private double CpuPercent()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var total = process.TotalProcessorTime;
            var now = DateTime.UtcNow;
            double percent = 0;
            lock (_gate)
            {
                if (_lastCpuAt != default)
                {
                    var wallMs = (now - _lastCpuAt).TotalMilliseconds;
                    if (wallMs > 0)
                    {
                        percent = (total - _lastCpuTime).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100.0;
                    }
                }
                _lastCpuTime = total;
                _lastCpuAt = now;
            }
            return Math.Clamp(percent, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        try { _timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { /* таймер уже остановлен */ }
        _timer?.Dispose();

        try { _queue.CompleteAdding(); } catch { /* очередь уже закрыта */ }
        try { _sender?.Join(5000); } catch { /* поток уже завершён */ }

        AgentLog.Info($"Агент завершён: тактов={Ticks}, активных секунд={ActiveSeconds:0.0}, отчётов={_reporter.ReportsTotal}, выборок={_reporter.SamplesTotal}, ошибок={_reporter.FailuresTotal}");
    }

    public void Dispose()
    {
        Stop();
        _queue.Dispose();
    }
}
