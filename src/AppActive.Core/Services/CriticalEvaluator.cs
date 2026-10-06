using AppActive.Core.Data;
using AppActive.Core.Models;
using AppActive.Core.Settings;

namespace AppActive.Core.Services;

/// <summary>
/// Считает, сколько времени проведено в приложении (или группе) за сутки и не превышен ли
/// заданный критический лимит. Дополнительно фиксирует события превышения в журнале.
/// </summary>
public sealed class CriticalEvaluator
{
    private readonly RulesStore _rules;
    private readonly ActivityStore _activity;
    private readonly SettingsService _settings;

    public CriticalEvaluator(RulesStore rules, ActivityStore activity, SettingsService settings)
    {
        _rules = rules;
        _activity = activity;
        _settings = settings;
    }

    public sealed class Target
    {
        public string Name { get; init; } = "";
        public List<long> AppIds { get; init; } = new();
    }

    public static int DayOfWeekNumber(string dayKey)
    {
        var local = DateTime.Parse(dayKey, System.Globalization.CultureInfo.InvariantCulture);
        var dow = (int)local.DayOfWeek; // 0 = воскресенье
        return dow == 0 ? 7 : dow;
    }

    public static bool AppliesOnDay(CriticalRuleRecord rule, string dayKey)
    {
        if (string.IsNullOrWhiteSpace(rule.DaysOfWeek)) return true;
        var days = rule.DaysOfWeek
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var v) ? v : 0)
            .Where(v => v is >= 1 and <= 7)
            .ToHashSet();
        if (days.Count == 0) return true;
        return days.Contains(DayOfWeekNumber(dayKey));
    }

    public Target ResolveTarget(CriticalRuleRecord rule, IReadOnlyDictionary<long, ApplicationRecord> apps,
        IReadOnlyDictionary<long, GroupRecord> groups, IReadOnlyDictionary<long, List<long>> groupMembers)
    {
        if (string.Equals(rule.TargetType, "group", StringComparison.OrdinalIgnoreCase) && rule.GroupId.HasValue)
        {
            var group = groups.TryGetValue(rule.GroupId.Value, out var g) ? g : null;
            var members = groupMembers.TryGetValue(rule.GroupId.Value, out var m) ? m : new List<long>();
            var name = group is null ? $"Группа #{rule.GroupId}" : $"{group.Name} ({members.Count} прил.)";
            return new Target { Name = name, AppIds = members.ToList() };
        }

        var appId = rule.ApplicationId ?? 0;
        var app = apps.TryGetValue(appId, out var a) ? a : null;
        var appName = app is null
            ? $"Приложение #{appId}"
            : string.Equals(app.DisplayName, app.ExeName, StringComparison.OrdinalIgnoreCase)
                ? app.ExeName
                : $"{app.DisplayName} ({app.ExeName})";
        return new Target { Name = appName, AppIds = new List<long> { appId } };
    }

    /// <summary>Статусы по всем правилам за указанные сутки.</summary>
    public List<CriticalStatus> Evaluate(
        string dayKey,
        IReadOnlyDictionary<long, double>? appSeconds = null,
        IReadOnlyDictionary<long, ApplicationRecord>? apps = null,
        IReadOnlyDictionary<long, GroupRecord>? groups = null,
        IReadOnlyDictionary<long, List<long>>? groupMembers = null)
    {
        appSeconds ??= _activity.GetAppSecondsForDay(dayKey);
        apps ??= _activity.ListApplications().ToDictionary(a => a.Id);
        groups ??= _activity.ListGroups().ToDictionary(g => g.Id);
        groupMembers ??= _activity.ListGroupMembers()
            .GroupBy(x => x.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.AppId).ToList());

        var result = new List<CriticalStatus>();
        foreach (var rule in _rules.ListCriticalRules())
        {
            var target = ResolveTarget(rule, apps, groups, groupMembers);
            var used = target.AppIds.Sum(id => appSeconds.TryGetValue(id, out var v) ? v : 0);
            var limit = rule.DailyLimitSeconds;
            var percent = limit > 0 ? used / limit * 100.0 : 0;
            var applicable = rule.Enabled && AppliesOnDay(rule, dayKey);
            var exceeded = applicable && limit > 0 && used >= limit;

            result.Add(new CriticalStatus
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                TargetType = rule.TargetType,
                TargetId = rule.TargetType == "group" ? rule.GroupId ?? 0 : rule.ApplicationId ?? 0,
                TargetName = target.Name,
                LimitSeconds = limit,
                UsedSeconds = used,
                Percent = Math.Round(percent, 1),
                Exceeded = exceeded,
                Severity = rule.Severity,
                Enabled = rule.Enabled,
                ApplicableToday = applicable,
                FirstExceededUtc = _rules.FirstEventUtc("critical", rule.Id, dayKey)
            });
        }

        return result;
    }

    /// <summary>
    /// Записывает событие «превышено критическое время». Одно событие на каждое пересечение
    /// порога (100%, 200%, …) за сутки, поэтому повторные проверки не засоряют журнал.
    /// </summary>
    public List<WorkflowEventRecord> EmitExceededEvents(string dayKey, IEnumerable<CriticalStatus> statuses)
    {
        var created = new List<WorkflowEventRecord>();
        foreach (var status in statuses)
        {
            if (!status.Exceeded || !status.Enabled || !status.ApplicableToday) continue;
            var crossings = (int)(status.UsedSeconds / Math.Max(1.0, status.LimitSeconds));
            var dedupKey = $"{dayKey}:{crossings}";
            if (_rules.EventExists("critical", status.RuleId, dedupKey)) continue;

            var record = new WorkflowEventRecord
            {
                OccurredUtc = TimeUtil.ToIso(DateTime.UtcNow),
                DayKey = dayKey,
                SourceType = "critical",
                SourceId = status.RuleId,
                RuleName = status.RuleName,
                Severity = status.Severity,
                Message = $"{status.TargetName}: {TimeUtil.FormatDuration(status.UsedSeconds)} из {TimeUtil.FormatDuration(status.LimitSeconds)} за сутки",
                TargetName = status.TargetName,
                UsedSeconds = Math.Round(status.UsedSeconds, 1),
                LimitSeconds = status.LimitSeconds,
                ActionType = "event",
                ActionStatus = "ok",
                DedupKey = dedupKey
            };
            record.Id = _rules.InsertEvent(record);
            created.Add(record);
        }

        return created;
    }

    public SettingsService Settings => _settings;
}
