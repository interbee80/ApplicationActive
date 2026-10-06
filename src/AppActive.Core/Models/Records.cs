namespace AppActive.Core.Models;

/// <summary>Строка таблицы Applications.</summary>
public sealed class ApplicationRecord
{
    public long Id { get; set; }
    public string AppKey { get; set; } = "";
    public string ExeName { get; set; } = "";
    public string? ExePath { get; set; }
    public string? FriendlyName { get; set; }
    public string? Category { get; set; }
    public bool IsIgnored { get; set; }
    public string FirstSeenUtc { get; set; } = "";
    public string LastSeenUtc { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? ExeName : FriendlyName!;
}

public sealed class GroupRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Color { get; set; }
    public string CreatedUtc { get; set; } = "";
}

public sealed class CriticalRuleRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>"app" | "group"</summary>
    public string TargetType { get; set; } = "app";
    public long? ApplicationId { get; set; }
    public long? GroupId { get; set; }
    public double DailyLimitSeconds { get; set; }
    public bool Enabled { get; set; } = true;
    public string Severity { get; set; } = "warning";
    /// <summary>"1,2,3,4,5" (1 = понедельник). null/пусто = все дни.</summary>
    public string? DaysOfWeek { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class WorkflowRuleRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>"criticalExceeded" | "schedule"</summary>
    public string TriggerType { get; set; } = "criticalExceeded";
    public long? CriticalRuleId { get; set; }
    public int ScheduleMinutes { get; set; } = 5;
    public string? TargetType { get; set; }
    public long? TargetId { get; set; }
    public double? ThresholdSeconds { get; set; }
    /// <summary>"event" | "script" | "webhook"</summary>
    public string ActionType { get; set; } = "event";
    public string? ActionTarget { get; set; }
    public int CooldownMinutes { get; set; } = 60;
    public string? LastRunUtc { get; set; }
    public string? LastStatus { get; set; }
    public long TriggerCount { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class WorkflowEventRecord
{
    public long Id { get; set; }
    public string OccurredUtc { get; set; } = "";
    public string DayKey { get; set; } = "";
    /// <summary>"critical" | "workflow"</summary>
    public string SourceType { get; set; } = "workflow";
    public long? SourceId { get; set; }
    public string RuleName { get; set; } = "";
    public string Severity { get; set; } = "info";
    public string Message { get; set; } = "";
    public string? TargetName { get; set; }
    public double? UsedSeconds { get; set; }
    public double? LimitSeconds { get; set; }
    public string? ActionType { get; set; }
    public string? ActionStatus { get; set; }
    public string? DedupKey { get; set; }
}

public sealed class SessionRecord
{
    public int SessionId { get; set; }
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string LastSeenUtc { get; set; } = "";
    public string State { get; set; } = "";
}

public sealed record AppUsageItem(long AppId, double Seconds);

public sealed record HourAppUsage(int HourIndex, long AppId, double Seconds);

public sealed record UserUsageItem(string UserName, double Seconds, IReadOnlyList<int> SessionIds);

public sealed record IntervalRecord(
    long Id,
    long AppId,
    string ExeName,
    string DisplayName,
    int SessionId,
    string UserName,
    string StartUtc,
    string EndUtc,
    double Seconds,
    string DayKey,
    int HourIndex);

/// <summary>Интервал для запросов по произвольному периоду (абсолютные времена, а не строки).</summary>
public sealed record RangeIntervalRow(
    long Id,
    long AppId,
    string ExeName,
    string DisplayName,
    int SessionId,
    string UserName,
    DateTime StartUtc,
    DateTime EndUtc,
    double DurationSeconds,
    string DayKey,
    int HourIndex)
{
    /// <summary>Длительность по стенным часам (может немного отличаться от учтённых секунд).</summary>
    public double WallSeconds => (EndUtc - StartUtc).TotalSeconds;

    /// <summary>
    /// Доля записи, попавшая в период: для полностью вошедшего интервала — все учтённые секунды,
    /// для частично пересекающегося — пропорционально пересечению.
    /// </summary>
    public double SecondsInRange(DateTime fromUtc, DateTime toUtc)
    {
        var start = StartUtc > fromUtc ? StartUtc : fromUtc;
        var end = EndUtc < toUtc ? EndUtc : toUtc;
        var overlap = (end - start).TotalSeconds;
        if (overlap <= 0) return 0;

        var wall = WallSeconds;
        if (wall <= 0.001) return DurationSeconds;

        return DurationSeconds * Math.Min(1.0, overlap / wall);
    }
}

/// <summary>Статус критического времени по правилу за сутки.</summary>
public sealed class CriticalStatus
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
