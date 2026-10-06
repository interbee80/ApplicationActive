namespace AppActive.Core.Models;

/// <summary>Ограничение критического времени, «приклеенное» к приложению или группе.</summary>
public sealed class CriticalLimitDto
{
    public long RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public double LimitSeconds { get; set; }
    public double UsedSeconds { get; set; }
    public double Percent { get; set; }
    public bool Exceeded { get; set; }
    public string Severity { get; set; } = "warning";
}

public sealed class AppListItemDto
{
    public long Id { get; set; }
    public string ExeName { get; set; } = "";
    public string? ExePath { get; set; }
    public string? FriendlyName { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Category { get; set; }
    public bool IsIgnored { get; set; }
    public string FirstSeenUtc { get; set; } = "";
    public string LastSeenUtc { get; set; } = "";
    public double TodaySeconds { get; set; }
    public double PercentOfTracked { get; set; }
    public int SessionCount { get; set; }
    public List<string> UserNames { get; set; } = new();
    public List<long> GroupIds { get; set; } = new();
    public List<string> GroupNames { get; set; } = new();
    public CriticalLimitDto? Critical { get; set; }
}

public sealed class GroupAppDto
{
    public long Id { get; set; }
    public string ExeName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double TodaySeconds { get; set; }
}

public sealed class GroupDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Color { get; set; }
    public List<long> AppIds { get; set; } = new();
    public int AppCount { get; set; }
    public double TodaySeconds { get; set; }
    public CriticalLimitDto? Critical { get; set; }
    public List<GroupAppDto> Apps { get; set; } = new();
}

public sealed class CriticalRuleDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string TargetType { get; set; } = "app";
    public long TargetId { get; set; }
    public string TargetName { get; set; } = "";
    public double DailyLimitSeconds { get; set; }
    public bool Enabled { get; set; } = true;
    public string Severity { get; set; } = "warning";
    public List<int>? DaysOfWeek { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class CriticalStatusDto
{
    public long RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public string TargetType { get; set; } = "app";
    public long TargetId { get; set; }
    public string TargetName { get; set; } = "";
    public double LimitSeconds { get; set; }
    public double UsedSeconds { get; set; }
    public double Percent { get; set; }
    public bool Exceeded { get; set; }
    public string Severity { get; set; } = "warning";
    public bool Enabled { get; set; } = true;
    public bool ApplicableToday { get; set; } = true;
    public string? FirstExceededUtc { get; set; }
}

public sealed class WorkflowRuleDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string TriggerType { get; set; } = "criticalExceeded";
    public long? CriticalRuleId { get; set; }
    public int ScheduleMinutes { get; set; } = 5;
    public string? TargetType { get; set; }
    public long? TargetId { get; set; }
    public double? ThresholdSeconds { get; set; }
    public string ActionType { get; set; } = "event";
    public string? ActionTarget { get; set; }
    public int CooldownMinutes { get; set; } = 60;
    public string? LastRunUtc { get; set; }
    public string? LastStatus { get; set; }
    public long TriggerCount { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class WorkflowEventDto
{
    public long Id { get; set; }
    public string OccurredUtc { get; set; } = "";
    public string DayKey { get; set; } = "";
    public long? RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public string Severity { get; set; } = "info";
    public string Message { get; set; } = "";
    public string? TargetName { get; set; }
    public double? UsedSeconds { get; set; }
    public double? LimitSeconds { get; set; }
    public string? ActionType { get; set; }
    public string? ActionStatus { get; set; }
}

public sealed class AgentStatusDto
{
    public int SessionId { get; set; }
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public int Pid { get; set; }
    public string? StartedUtc { get; set; }
    public string? LastReportUtc { get; set; }
    public bool Alive { get; set; }
    public long ReportsTotal { get; set; }
    public double CpuPercent { get; set; }
    public double WorkingSetMb { get; set; }
    public string? AgentVersion { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class DashboardSettingsDto
{
    public int PollIntervalSeconds { get; set; }
    public int ActivityIdleSeconds { get; set; }
    public bool MonitorEnabled { get; set; }
    public int RetentionDays { get; set; }
}

public sealed class DashboardTotalsDto
{
    public double TrackedSeconds { get; set; }
    public int AppCount { get; set; }
    public int SessionCount { get; set; }
    public int UserCount { get; set; }
    public int CriticalExceededCount { get; set; }
    public int CriticalRuleCount { get; set; }
    public int MonitoredAppCount { get; set; }
}

public sealed class DashboardHourAppDto
{
    public long AppId { get; set; }
    public string ExeName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double Seconds { get; set; }
}

public sealed class DashboardHourDto
{
    public int Hour { get; set; }
    public double TotalSeconds { get; set; }
    public bool IsCurrent { get; set; }
    public List<DashboardHourAppDto> Apps { get; set; } = new();
}

/// <summary>Столбец графика: час при коротком периоде или сутки при длинном.</summary>
public sealed class DashboardBucketDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string StartUtc { get; set; } = "";
    public string EndUtc { get; set; } = "";
    public bool IsCurrent { get; set; }
    public bool IsPartial { get; set; }
    public double TotalSeconds { get; set; }
    public List<DashboardHourAppDto> Apps { get; set; } = new();
}

/// <summary>Описание выбранного периода (в том числе введённого как now-1d).</summary>
public sealed class DashboardRangeDto
{
    public string FromUtc { get; set; } = "";
    public string ToUtc { get; set; } = "";
    public string FromLocal { get; set; } = "";
    public string ToLocal { get; set; } = "";
    public string FromText { get; set; } = "";
    public string ToText { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>"hour" — столбцы по часам, "day" — по суткам.</summary>
    public string BucketKind { get; set; } = "hour";
    public int BucketCount { get; set; }
    public double TotalSeconds { get; set; }
}

public sealed class ColorizedProcessDto
{
    public int Pid { get; set; }
    public string ExeName { get; set; } = "";
    public string? ExePath { get; set; }
    public int SessionId { get; set; }
    public string SessionName { get; set; } = "";
    public string? StartedUtc { get; set; }
    public double MemoryMb { get; set; }
    public string WindowTitle { get; set; } = "";
    public bool IsProtected { get; set; }
    public string? ProtectedReason { get; set; }
}

public sealed class ProcessKillResultDto
{
    public int Pid { get; set; }
    public string ExeName { get; set; } = "";
    public int SessionId { get; set; }
    public string Status { get; set; } = "";
    public string? Message { get; set; }
}

public sealed class DashboardAppDto
{
    public long AppId { get; set; }
    public string ExeName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? FriendlyName { get; set; }
    public string? ExePath { get; set; }
    public double Seconds { get; set; }
    public double Percent { get; set; }
    public int SessionCount { get; set; }
    public List<string> UserNames { get; set; } = new();
    public List<long> GroupIds { get; set; } = new();
    public List<string> GroupNames { get; set; } = new();
    public CriticalLimitDto? Critical { get; set; }
}

public sealed class DashboardUserDto
{
    public string UserName { get; set; } = "";
    public double Seconds { get; set; }
    public double Percent { get; set; }
    public List<int> SessionIds { get; set; } = new();
}

public sealed class DashboardTimelineItemDto
{
    public long Id { get; set; }
    public long AppId { get; set; }
    public string ExeName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string UserName { get; set; } = "";
    public int SessionId { get; set; }
    public string StartUtc { get; set; } = "";
    public string EndUtc { get; set; } = "";
    public double Seconds { get; set; }
    public int HourIndex { get; set; }
}

public sealed class DashboardPayload
{
    public string Date { get; set; } = "";
    public string GeneratedUtc { get; set; } = "";
    public DashboardSettingsDto Settings { get; set; } = new();
    public DashboardTotalsDto Totals { get; set; } = new();

    /// <summary>Выбранный период (в том числе относительный: now-1d … now).</summary>
    public DashboardRangeDto Range { get; set; } = new();

    /// <summary>Столбцы графика: по часам или по суткам в зависимости от длины периода.</summary>
    public List<DashboardBucketDto> Buckets { get; set; } = new();

    /// <summary>Совместимость: столбцы по часам внутри одних суток.</summary>
    public List<DashboardHourDto> Hours { get; set; } = new();

    /// <summary>Цвета приложений — одни и те же во всех блоках панели.</summary>
    public Dictionary<string, string> AppColors { get; set; } = new();

    public List<DashboardAppDto> TopApps { get; set; } = new();
    public List<DashboardUserDto> PerUser { get; set; } = new();
    public List<CriticalStatusDto> Critical { get; set; } = new();
    public List<WorkflowEventDto> Events { get; set; } = new();
    public List<DashboardTimelineItemDto> Timeline { get; set; } = new();
    public List<AgentStatusDto> Agents { get; set; } = new();
}

public sealed class SettingsDto
{
    public int PollIntervalSeconds { get; set; }
    public int ActivityIdleSeconds { get; set; }
    public int FlushSeconds { get; set; }
    public int RetentionDays { get; set; }
    public bool MonitorEnabled { get; set; }
    public bool CollectWindowTitles { get; set; }
    public List<string> IgnoreProcesses { get; set; } = new();
    public int AgentHeartbeatSeconds { get; set; }
    public bool RequiresAdminKey { get; set; }
    public string UpdatedUtc { get; set; } = "";
}
