using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AppActive.Core.Data;
using AppActive.Core.Models;
using AppActive.Core.Options;

namespace AppActive.Core.Services;

public sealed class WorkflowActionResult
{
    public string RuleName { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class WorkflowRunResult
{
    public string Date { get; set; } = "";
    public string EvaluatedAt { get; set; } = "";
    public int EventsCreated { get; set; }
    public List<WorkflowActionResult> Actions { get; set; } = new();
}

/// <summary>
/// Workflow-движок: по расписанию проверяет правила критического времени и правила реакции
/// (событие в журнал / запуск скрипта / webhook).
/// </summary>
public sealed class WorkflowEngine
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly RulesStore _rules;
    private readonly ActivityStore _activity;
    private readonly CriticalEvaluator _critical;
    private readonly AppActiveOptions _options;

    public WorkflowEngine(RulesStore rules, ActivityStore activity, CriticalEvaluator critical, AppActiveOptions options)
    {
        _rules = rules;
        _activity = activity;
        _critical = critical;
        _options = options;
    }

    public DateTime? LastRunUtc { get; private set; }

    public WorkflowRunResult LastResult { get; private set; } = new();

    public WorkflowRunResult Run(string dayKey, bool force)
    {
        var apps = _activity.ListApplications().ToDictionary(a => a.Id);
        var groupMembers = _activity.ListGroupMembers()
            .GroupBy(x => x.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.AppId).ToList());
        var appSeconds = _activity.GetAppSecondsForDay(dayKey);

        var statuses = _critical.Evaluate(dayKey, appSeconds, apps, _activity.ListGroups().ToDictionary(g => g.Id), groupMembers);
        var created = _critical.EmitExceededEvents(dayKey, statuses);

        var result = new WorkflowRunResult
        {
            Date = dayKey,
            EvaluatedAt = TimeUtil.ToIso(DateTime.UtcNow),
            EventsCreated = created.Count
        };

        foreach (var rule in _rules.ListWorkflowRules())
        {
            if (!rule.Enabled) continue;
            try
            {
                if (string.Equals(rule.TriggerType, "criticalExceeded", StringComparison.OrdinalIgnoreCase))
                {
                    var status = statuses.FirstOrDefault(s => s.RuleId == rule.CriticalRuleId);
                    if (status is null || !status.Exceeded || !status.ApplicableToday) continue;
                    if (!force && !CooldownElapsed(rule)) continue;

                    result.Actions.Add(Execute(rule, dayKey, status.TargetName, status.UsedSeconds, status.LimitSeconds,
                        status.Percent, status.Severity));
                }
                else if (string.Equals(rule.TriggerType, "schedule", StringComparison.OrdinalIgnoreCase))
                {
                    if (!force && !ScheduleElapsed(rule)) continue;

                    var (targetName, used) = Measure(rule, dayKey, apps, groupMembers, appSeconds);
                    var threshold = rule.ThresholdSeconds ?? 0;
                    if (threshold > 0 && used < threshold)
                    {
                        // Условие не выполнено — просто отмечаем время проверки.
                        _rules.MarkWorkflowChecked(rule.Id);
                        continue;
                    }

                    var percent = threshold > 0 ? Math.Round(used / threshold * 100.0, 1) : 0;
                    result.Actions.Add(Execute(rule, dayKey, targetName, used, threshold, percent, "info"));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"Workflow: правило '{rule.Name}' завершилось ошибкой: {ex.Message}", ex);
                result.Actions.Add(new WorkflowActionResult
                {
                    RuleName = rule.Name,
                    ActionType = rule.ActionType,
                    Status = "failed",
                    Message = ex.Message
                });
            }
        }

        LastRunUtc = DateTime.UtcNow;
        LastResult = result;
        return result;
    }

    private static bool CooldownElapsed(WorkflowRuleRecord rule)
    {
        var last = TimeUtil.TryParseIso(rule.LastRunUtc);
        if (!last.HasValue) return true;
        return (DateTime.UtcNow - last.Value).TotalMinutes >= Math.Max(1, rule.CooldownMinutes);
    }

    private static bool ScheduleElapsed(WorkflowRuleRecord rule)
    {
        var last = TimeUtil.TryParseIso(rule.LastRunUtc);
        if (!last.HasValue) return true;
        return (DateTime.UtcNow - last.Value).TotalMinutes >= Math.Max(1, rule.ScheduleMinutes);
    }

    private (string TargetName, double UsedSeconds) Measure(
        WorkflowRuleRecord rule,
        string dayKey,
        IReadOnlyDictionary<long, ApplicationRecord> apps,
        IReadOnlyDictionary<long, List<long>> groupMembers,
        IReadOnlyDictionary<long, double> appSeconds)
    {
        _ = dayKey;
        if (string.Equals(rule.TargetType, "group", StringComparison.OrdinalIgnoreCase) && rule.TargetId.HasValue)
        {
            var group = _activity.GetGroup(rule.TargetId.Value);
            var members = groupMembers.TryGetValue(rule.TargetId.Value, out var m) ? m : new List<long>();
            var used = members.Sum(id => appSeconds.TryGetValue(id, out var v) ? v : 0);
            return (group?.Name ?? $"Группа #{rule.TargetId}", used);
        }

        var appId = rule.TargetId ?? 0;
        var app = apps.TryGetValue(appId, out var a) ? a : null;
        var name = app is null ? $"Приложение #{appId}" : $"{app.DisplayName} ({app.ExeName})";
        return (name, appSeconds.TryGetValue(appId, out var seconds) ? seconds : 0);
    }

    private WorkflowActionResult Execute(WorkflowRuleRecord rule, string dayKey, string targetName,
        double usedSeconds, double limitSeconds, double percent, string severity)
    {
        var message = $"Правило «{rule.Name}»: {targetName} — {TimeUtil.FormatDuration(usedSeconds)}"
                      + (limitSeconds > 0 ? $" из {TimeUtil.FormatDuration(limitSeconds)} ({percent:0.#}%)" : "");
        var status = "ok";
        var detail = "";

        switch ((rule.ActionType ?? "event").ToLowerInvariant())
        {
            case "script":
                if (!_options.Workflow.AllowScriptActions)
                {
                    status = "skipped";
                    detail = "действия типа script выключены (AppActive:Workflow:AllowScriptActions=false)";
                }
                else if (string.IsNullOrWhiteSpace(rule.ActionTarget))
                {
                    status = "failed";
                    detail = "не задана команда (actionTarget)";
                }
                else
                {
                    (status, detail) = RunScript(Substitute(rule.ActionTarget!, rule, dayKey, targetName, usedSeconds, limitSeconds, percent));
                }
                break;

            case "webhook":
                if (string.IsNullOrWhiteSpace(rule.ActionTarget))
                {
                    status = "failed";
                    detail = "не задан адрес webhook (actionTarget)";
                }
                else
                {
                    (status, detail) = SendWebhook(rule, dayKey, targetName, usedSeconds, limitSeconds, percent, severity, message);
                }
                break;
        }

        var record = new WorkflowEventRecord
        {
            OccurredUtc = TimeUtil.ToIso(DateTime.UtcNow),
            DayKey = dayKey,
            SourceType = "workflow",
            SourceId = rule.Id,
            RuleName = rule.Name,
            Severity = severity,
            Message = detail.Length > 0 ? $"{message} [{detail}]" : message,
            TargetName = targetName,
            UsedSeconds = Math.Round(usedSeconds, 1),
            LimitSeconds = limitSeconds,
            ActionType = rule.ActionType,
            ActionStatus = status,
            DedupKey = null
        };
        _rules.InsertEvent(record);
        _rules.MarkWorkflowRun(rule.Id, status);

        if (status != "ok")
        {
            AppLog.Warn($"Workflow «{rule.Name}»: {status} — {detail}");
        }

        return new WorkflowActionResult
        {
            RuleName = rule.Name,
            ActionType = rule.ActionType ?? "event",
            Status = status,
            Message = detail
        };
    }

    private static string Substitute(string template, WorkflowRuleRecord rule, string dayKey, string targetName,
        double used, double limit, double percent) => template
        .Replace("{date}", dayKey, StringComparison.OrdinalIgnoreCase)
        .Replace("{target}", targetName, StringComparison.OrdinalIgnoreCase)
        .Replace("{used}", ((int)Math.Round(used)).ToString(), StringComparison.OrdinalIgnoreCase)
        .Replace("{limit}", ((int)Math.Round(limit)).ToString(), StringComparison.OrdinalIgnoreCase)
        .Replace("{percent}", percent.ToString("0.#"), StringComparison.OrdinalIgnoreCase)
        .Replace("{rule}", rule.Name, StringComparison.OrdinalIgnoreCase);

    private static (string Status, string Detail) RunScript(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(psi);
            if (process is null) return ("failed", "не удалось запустить процесс");
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(true); } catch { /* процесс уже завершился */ }
                return ("timeout", "процесс не завершился за 15 с и был остановлен");
            }
            return process.ExitCode == 0
                ? ("ok", $"код возврата 0")
                : ("failed", $"код возврата {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return ("failed", ex.Message);
        }
    }

    private (string Status, string Detail) SendWebhook(WorkflowRuleRecord rule, string dayKey, string targetName,
        double used, double limit, double percent, string severity, string message)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                rule = rule.Name,
                ruleId = rule.Id,
                date = dayKey,
                target = targetName,
                usedSeconds = Math.Round(used, 1),
                limitSeconds = limit,
                percent,
                severity,
                message,
                occurredUtc = TimeUtil.ToIso(DateTime.UtcNow)
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(2, _options.Workflow.WebhookTimeoutSeconds)));
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = Http.PostAsync(rule.ActionTarget!, content, cts.Token).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode
                ? ("ok", $"HTTP {(int)response.StatusCode}")
                : ("failed", $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return ("failed", ex.Message);
        }
    }
}
