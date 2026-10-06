using System.Diagnostics;
using AppActive.Core;
using AppActive.Core.Data;
using AppActive.Core.Options;
using AppActive.Core.Services;

namespace AppActive.Service.Monitoring;

/// <summary>Показатели самой службы (используются в /api/diagnostics).</summary>
internal sealed class ServiceMetrics
{
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private int _lastThreadCount;

    public DateTime StartedUtc => _startedUtc;

    public double UptimeSeconds => (DateTime.UtcNow - _startedUtc).TotalSeconds;

    /// <summary>Средняя загрузка ЦП службы с момента предыдущего вызова (в процентах от одного ядра).</summary>
    public double SampleCpuPercent()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var total = process.TotalProcessorTime;
            var now = DateTime.UtcNow;
            var wallMs = (now - _lastSampleUtc).TotalMilliseconds;
            _lastThreadCount = process.Threads.Count;
            double percent = 0;
            if (wallMs > 0 && _lastCpu != TimeSpan.Zero)
            {
                percent = (total - _lastCpu).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100.0;
            }
            _lastCpu = total;
            _lastSampleUtc = now;
            return Math.Clamp(percent, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    public object Snapshot(double cpuPercent)
    {
        long workingSet = 0;
        int threads = _lastThreadCount;
        int handles = 0;
        long gcHeap = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            workingSet = process.WorkingSet64;
            threads = process.Threads.Count;
            handles = process.HandleCount;
            gcHeap = GC.GetTotalMemory(false);
        }
        catch
        {
            // показатели недоступны — не критично
        }

        return new
        {
            cpuPercent = Math.Round(cpuPercent, 3),
            workingSetMb = Math.Round(workingSet / 1024.0 / 1024.0, 1),
            threadCount = threads,
            handleCount = handles,
            gcHeapMb = Math.Round(gcHeap / 1024.0 / 1024.0, 1),
            uptimeSeconds = Math.Round(UptimeSeconds, 0),
            startedUtc = TimeUtil.ToIso(_startedUtc)
        };
    }
}

/// <summary>
/// Фоновый цикл службы: периодический сброс накопленной активности в SQLite,
/// прогон Workflow-правил и обслуживание базы (хранение по retentionDays).
/// </summary>
internal sealed class MonitoringWorker : BackgroundService
{
    private readonly AppActiveOptions _options;
    private readonly ActivityAccumulator _accumulator;
    private readonly SettingsService _settings;
    private readonly WorkflowEngine _workflow;
    private readonly ActivityStore _activity;
    private readonly RulesStore _rules;
    private readonly ILogger<MonitoringWorker> _logger;

    public MonitoringWorker(AppActiveOptions options, ActivityAccumulator accumulator, SettingsService settings,
        WorkflowEngine workflow, ActivityStore activity, RulesStore rules, ILogger<MonitoringWorker> logger)
    {
        _options = options;
        _accumulator = accumulator;
        _settings = settings;
        _workflow = workflow;
        _activity = activity;
        _rules = rules;
        _logger = logger;
    }

    public long FlushCount { get; private set; }
    public DateTime? LastMaintenanceUtc { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AppLog.Info("Фоновый цикл службы запущен (сброс активности, правила Workflow, обслуживание базы)");

        var lastFlush = DateTime.UtcNow;
        var lastWorkflow = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var settings = _settings.Current;

                if ((now - lastFlush).TotalSeconds >= Math.Clamp(settings.FlushSeconds, 1, 600))
                {
                    var rows = _accumulator.Flush();
                    FlushCount++;
                    lastFlush = now;
                    if (rows > 0) _logger.LogDebug("Сброшено интервалов: {Rows}", rows);
                }

                if ((now - lastWorkflow).TotalSeconds >= Math.Clamp(_options.Workflow.EvaluationSeconds, 10, 3600))
                {
                    var result = _workflow.Run(TimeUtil.TodayKey(), force: false);
                    lastWorkflow = now;
                    if (result.EventsCreated > 0 || result.Actions.Count > 0)
                    {
                        AppLog.Info($"Workflow: новых событий {result.EventsCreated}, действий {result.Actions.Count}");
                    }
                }

                if (LastMaintenanceUtc is null || (now - LastMaintenanceUtc.Value).TotalHours >= 6)
                {
                    Maintenance(now);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в фоновом цикле службы");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Не теряем накопленное при остановке службы.
        try
        {
            _accumulator.FlushAndReset();
            AppLog.Info("Накопленная активность записана в базу при остановке службы");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось записать активность при остановке");
        }
    }

    private void Maintenance(DateTime now)
    {
        try
        {
            var retention = Math.Clamp(_settings.Current.RetentionDays, 1, 3650);
            var cutoff = TimeUtil.DayKey(now.AddDays(-retention));
            var removedIntervals = _activity.PurgeBefore(cutoff);
            var removedEvents = _rules.DeleteEventsBefore(cutoff);
            var removedApps = _activity.PurgeApplicationsNeverUsed(TimeUtil.ToIso(now.AddDays(-30)));
            LastMaintenanceUtc = now;
            if (removedIntervals + removedEvents + removedApps > 0)
            {
                AppLog.Info($"Обслуживание базы: удалено интервалов {removedIntervals}, событий {removedEvents}, приложений {removedApps} (хранение {retention} сут)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Обслуживание базы не выполнено: {Error}", ex.Message);
        }
    }
}
