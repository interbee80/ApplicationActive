namespace AppActive.Service.Api;

/// <summary>Тела запросов Web API.</summary>
internal sealed class SettingsPatch
{
    public int? PollIntervalSeconds { get; set; }
    public int? ActivityIdleSeconds { get; set; }
    public int? FlushSeconds { get; set; }
    public int? RetentionDays { get; set; }
    public bool? MonitorEnabled { get; set; }
    public bool? CollectWindowTitles { get; set; }
    public List<string>? IgnoreProcesses { get; set; }
    public int? AgentHeartbeatSeconds { get; set; }
}

internal sealed class AppPatch
{
    public string? FriendlyName { get; set; }
    public string? Category { get; set; }
    public bool? IsIgnored { get; set; }
}

internal sealed class GroupUpsertRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
    public List<long>? AppIds { get; set; }
}

internal sealed class CriticalRuleUpsertRequest
{
    public string? Name { get; set; }
    public string? TargetType { get; set; }
    public long? TargetId { get; set; }
    public double? DailyLimitSeconds { get; set; }
    public bool? Enabled { get; set; }
    public string? Severity { get; set; }
    public List<int>? DaysOfWeek { get; set; }
}

internal sealed class WorkflowRuleUpsertRequest
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? TriggerType { get; set; }
    public long? CriticalRuleId { get; set; }
    public int? ScheduleMinutes { get; set; }
    public string? TargetType { get; set; }
    public long? TargetId { get; set; }
    public double? ThresholdSeconds { get; set; }
    public string? ActionType { get; set; }
    public string? ActionTarget { get; set; }
    public int? CooldownMinutes { get; set; }
}

internal sealed class WorkflowRunRequest
{
    public string? Date { get; set; }
}

internal sealed class IngestSample
{
    public string AppKey { get; set; } = "";
    public string ExeName { get; set; } = "";
    public string? ExePath { get; set; }
    public string? WindowTitle { get; set; }
    public DateTime StartUtc { get; set; }
    public double Seconds { get; set; }
}

/// <summary>Запрос на завершение процесса приложения.</summary>
internal sealed class KillProcessRequest
{
    /// <summary>Идентификатор приложения в базе.</summary>
    public long AppId { get; set; }

    /// <summary>Конкретный процесс; если не задан — завершаются все процессы приложения.</summary>
    public int? Pid { get; set; }

    public string? Reason { get; set; }
}

internal sealed class IngestReportRequest
{
    public int SessionId { get; set; }
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public int Pid { get; set; }
    public string AgentVersion { get; set; } = "";
    public double CpuPercent { get; set; }
    public double WorkingSetMb { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<IngestSample> Samples { get; set; } = new();
}
