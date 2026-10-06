using System.Runtime.InteropServices;
using System.Text;
using AppActive.Core;
using AppActive.Core.Data;
using AppActive.Core.Models;
using AppActive.Core.Options;
using AppActive.Core.Services;
using AppActive.Core.Settings;
using AppActive.Service.Monitoring;
using AppActive.Service.Windows;
using Microsoft.Data.Sqlite;

namespace AppActive.Service.Api;

/// <summary>Все REST-методы Web-панели и приёма данных от агентов.</summary>
internal static class ApiEndpoints
{
    private static readonly string[] AllowedSeverities = { "info", "warning", "critical" };

    public static string Version => typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static void Map(WebApplication app, AppActiveOptions options)
    {
        MapSystem(app, options);
        MapSettings(app, options);
        MapApplications(app, options);
        MapGroups(app, options);
        MapCritical(app, options);
        MapWorkflow(app, options);
        MapDashboard(app, options);
        MapIngest(app, options);
    }

    // =============================================================== служебные

    private static void MapSystem(WebApplication app, AppActiveOptions options)
    {
        app.MapGet("/api/health", (HttpContext ctx, AgentRegistry registry, SettingsService settings,
            ServiceMetrics metrics, ActivityStore activity, IngestCounters counters) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            var current = settings.Current;
            return Results.Json(new
            {
                status = "ok",
                version = Version,
                startedUtc = TimeUtil.ToIso(metrics.StartedUtc),
                uptimeSeconds = Math.Round(metrics.UptimeSeconds, 0),
                machineName = Environment.MachineName,
                databasePath = activity.DatabasePath,
                databaseSizeBytes = activity.Database.GetFileSizeBytes(),
                monitorEnabled = current.MonitorEnabled,
                pollIntervalSeconds = current.PollIntervalSeconds,
                activityIdleSeconds = current.ActivityIdleSeconds,
                lastIngestUtc = counters.LastReportUtcIso,
                agents = registry.GetAgentStatuses(true)
            });
        });

        app.MapGet("/api/version", (HttpContext ctx) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;
            return Results.Json(new
            {
                version = Version,
                databaseSchemaVersion = SqliteDatabase.SchemaVersion,
                framework = RuntimeInformation.FrameworkDescription,
                machineName = Environment.MachineName,
                osVersion = Environment.OSVersion.VersionString,
                timeZone = TimeZoneInfo.Local.DisplayName,
                startedUtc = TimeUtil.ToIso(DateTime.UtcNow)
            });
        });

        app.MapGet("/api/diagnostics", (HttpContext ctx, ServiceMetrics metrics, IngestCounters counters,
            AgentRegistry registry, SettingsService settings, ActivityAccumulator accumulator,
            MonitoringWorker worker, ActivityStore activity, RulesStore rules, WorkflowEngine workflow) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            var current = settings.Current;
            var today = TimeUtil.TodayKey();
            return Results.Json(new
            {
                service = metrics.Snapshot(metrics.SampleCpuPercent()),
                ingest = counters.Snapshot(),
                monitoring = new
                {
                    enabled = current.MonitorEnabled,
                    pollIntervalSeconds = current.PollIntervalSeconds,
                    activityIdleSeconds = current.ActivityIdleSeconds,
                    flushSeconds = current.FlushSeconds,
                    pendingBuckets = accumulator.PendingBuckets,
                    pendingSeconds = Math.Round(accumulator.PendingSeconds, 1),
                    flushesTotal = worker.FlushCount,
                    lastFlushUtc = accumulator.LastFlushUtc.HasValue ? TimeUtil.ToIso(accumulator.LastFlushUtc.Value) : null,
                    samplesAccepted = accumulator.SamplesAccepted,
                    secondsAccepted = Math.Round(accumulator.SecondsAccepted, 1),
                    samplesRejected = accumulator.SamplesRejected,
                    lastMaintenanceUtc = worker.LastMaintenanceUtc.HasValue ? TimeUtil.ToIso(worker.LastMaintenanceUtc.Value) : null
                },
                workflow = new
                {
                    rulesTotal = rules.ListWorkflowRules().Count,
                    criticalRulesTotal = rules.ListCriticalRules().Count,
                    lastRunUtc = workflow.LastRunUtc.HasValue ? TimeUtil.ToIso(workflow.LastRunUtc.Value) : null,
                    lastEventsCreated = workflow.LastResult.EventsCreated,
                    scriptActionsEnabled = options.Workflow.AllowScriptActions,
                    evaluationSeconds = options.Workflow.EvaluationSeconds
                },
                database = new
                {
                    path = activity.DatabasePath,
                    sizeBytes = activity.Database.GetFileSizeBytes(),
                    walSizeBytes = activity.Database.GetWalSizeBytes(),
                    intervalsToday = activity.CountIntervalsForDay(today),
                    applicationsTotal = activity.CountApplications(),
                    groupsTotal = activity.ListGroups().Count
                },
                agents = registry.GetAgentStatuses(false)
            });
        });

        app.MapGet("/api/sessions", (HttpContext ctx, ActivityStore activity) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;
            return Results.Json(new { sessions = activity.ListSessions() });
        });
    }

    // =============================================================== настройки

    private static void MapSettings(WebApplication app, AppActiveOptions options)
    {
        app.MapGet("/api/settings", (HttpContext ctx, SettingsService settings) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;
            return Results.Json(ToSettingsDto(settings.Current, options));
        });

        app.MapPut("/api/settings", (HttpContext ctx, SettingsPatch patch, SettingsService settings) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var updated = settings.Update(s =>
            {
                if (patch.PollIntervalSeconds.HasValue) s.PollIntervalSeconds = patch.PollIntervalSeconds.Value;
                if (patch.ActivityIdleSeconds.HasValue) s.ActivityIdleSeconds = patch.ActivityIdleSeconds.Value;
                if (patch.FlushSeconds.HasValue) s.FlushSeconds = patch.FlushSeconds.Value;
                if (patch.RetentionDays.HasValue) s.RetentionDays = patch.RetentionDays.Value;
                if (patch.MonitorEnabled.HasValue) s.MonitorEnabled = patch.MonitorEnabled.Value;
                if (patch.CollectWindowTitles.HasValue) s.CollectWindowTitles = patch.CollectWindowTitles.Value;
                if (patch.AgentHeartbeatSeconds.HasValue) s.AgentHeartbeatSeconds = patch.AgentHeartbeatSeconds.Value;
                if (patch.IgnoreProcesses is not null) s.IgnoreProcesses = patch.IgnoreProcesses.ToList();
            });

            AppLog.Info($"Настройки обновлены через Web-панель: интервал опроса {updated.PollIntervalSeconds} с, " +
                        $"N={updated.ActivityIdleSeconds} с, сброс {updated.FlushSeconds} с, хранение {updated.RetentionDays} сут, " +
                        $"учёт {(updated.MonitorEnabled ? "включён" : "выключен")}");

            return Results.Json(ToSettingsDto(updated, options));
        });
    }

    private static SettingsDto ToSettingsDto(AppSettings settings, AppActiveOptions options) => new()
    {
        PollIntervalSeconds = settings.PollIntervalSeconds,
        ActivityIdleSeconds = settings.ActivityIdleSeconds,
        FlushSeconds = settings.FlushSeconds,
        RetentionDays = settings.RetentionDays,
        MonitorEnabled = settings.MonitorEnabled,
        CollectWindowTitles = settings.CollectWindowTitles,
        IgnoreProcesses = settings.IgnoreProcesses.ToList(),
        AgentHeartbeatSeconds = settings.AgentHeartbeatSeconds,
        RequiresAdminKey = !string.IsNullOrWhiteSpace(options.Security.AdminKey),
        UpdatedUtc = settings.UpdatedUtc
    };

    // =============================================================== приложения

    private static void MapApplications(WebApplication app, AppActiveOptions options)
    {
        app.MapGet("/api/apps", (HttpContext ctx, string? date, string? search, string? sort, string? from, string? to,
            DashboardQueryService query) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            var range = ResolveRange(from, to, out var rangeError);
            if (rangeError is not null) return BadRequest(rangeError);

            var dayKey = ResolveDate(date, range);
            var apps = query.BuildApps(dayKey, search, sort, range);
            return Results.Json(new
            {
                date = dayKey,
                trackedSeconds = Math.Round(apps.Where(a => !a.IsIgnored).Sum(a => a.TodaySeconds), 1),
                range = range is null ? null : new
                {
                    fromUtc = TimeUtil.ToIso(range.FromUtc),
                    toUtc = TimeUtil.ToIso(range.ToUtc),
                    fromLocal = TimeUtil.ToLocal(range.FromUtc).ToString("yyyy-MM-dd HH:mm"),
                    toLocal = TimeUtil.ToLocal(range.ToUtc).ToString("yyyy-MM-dd HH:mm"),
                    label = range.Label
                },
                apps
            });
        });

        app.MapPut("/api/apps/{id:long}", (HttpContext ctx, long id, AppPatch patch, ActivityStore activity, DashboardQueryService query) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var existing = activity.GetApplication(id);
            if (existing is null) return NotFound($"Приложение #{id} не найдено");

            activity.UpdateApplication(id,
                patch.FriendlyName,
                patch.FriendlyName is not null,
                patch.Category,
                patch.Category is not null,
                patch.IsIgnored);

            var updated = query.BuildApps(TimeUtil.TodayKey(), null, null).FirstOrDefault(a => a.Id == id);
            return updated is null ? NotFound($"Приложение #{id} не найдено") : Results.Json(updated);
        });

        app.MapGet("/api/apps/{id:long}/intervals", (HttpContext ctx, long id, string? date, string? from, string? to,
            ActivityStore activity) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            var range = ResolveRange(from, to, out var rangeError);
            if (rangeError is not null) return BadRequest(rangeError);

            if (range is not null)
            {
                var rows = activity.GetIntervalsInRange(range.FromUtc, range.ToUtc)
                    .Where(row => row.AppId == id)
                    .Select(row => new
                    {
                        id = row.Id,
                        startUtc = TimeUtil.ToIso(row.StartUtc > range.FromUtc ? row.StartUtc : range.FromUtc),
                        endUtc = TimeUtil.ToIso(row.EndUtc < range.ToUtc ? row.EndUtc : range.ToUtc),
                        seconds = Math.Round(row.SecondsInRange(range.FromUtc, range.ToUtc), 1),
                        hourIndex = row.HourIndex,
                        sessionId = row.SessionId,
                        userName = row.UserName,
                        dayKey = row.DayKey
                    })
                    .Where(row => row.seconds > 0)
                    .ToList();

                return Results.Json(new
                {
                    appId = id,
                    date = ResolveDate(date, range),
                    from = range.FromText,
                    to = range.ToText,
                    label = range.Label,
                    totalSeconds = Math.Round(rows.Sum(r => r.seconds), 1),
                    intervals = rows
                });
            }

            var dayKey = ResolveDate(date, null);
            var intervals = activity.GetIntervalsForApp(id, dayKey);
            return Results.Json(new
            {
                appId = id,
                date = dayKey,
                from = (string?)null,
                to = (string?)null,
                label = dayKey,
                totalSeconds = Math.Round(intervals.Sum(i => i.Seconds), 1),
                intervals = intervals.Select(i => new
                {
                    id = i.Id,
                    startUtc = i.StartUtc,
                    endUtc = i.EndUtc,
                    seconds = Math.Round(i.Seconds, 1),
                    hourIndex = i.HourIndex,
                    sessionId = i.SessionId,
                    userName = i.UserName,
                    dayKey = i.DayKey
                })
            });
        });

        // ------------------------------------------------------------ процессы
        app.MapGet("/api/processes", (HttpContext ctx, long appId, ActivityStore activity, ProcessManager processes) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            var app = activity.GetApplication(appId);
            if (app is null) return NotFound($"Приложение #{appId} не найдено");

            var list = processes.ListForApplication(app);
            return Results.Json(new
            {
                appId,
                displayName = app.DisplayName,
                exeName = app.ExeName,
                exePath = app.ExePath,
                canKill = list.Any(p => !p.IsProtected),
                processes = list
            });
        });

        app.MapPost("/api/processes/kill", (HttpContext ctx, KillProcessRequest request, ActivityStore activity,
            ProcessManager processes) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var app = activity.GetApplication(request.AppId);
            if (app is null) return NotFound($"Приложение #{request.AppId} не найдено");

            var outcome = processes.Kill(app, request.Pid, request.Reason, ctx.Connection.RemoteIpAddress?.ToString() ?? "локально");
            return Results.Json(new
            {
                ok = outcome.KilledCount > 0,
                appId = app.Id,
                displayName = app.DisplayName,
                exeName = app.ExeName,
                killedCount = outcome.KilledCount,
                results = outcome.Results
            });
        });
    }

    // =============================================================== группы

    private static void MapGroups(WebApplication app, AppActiveOptions options)
    {
        app.MapGet("/api/groups", (HttpContext ctx, string? date, DashboardQueryService query) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;
            return Results.Json(new { date = ResolveDate(date), groups = query.BuildGroups(ResolveDate(date)) });
        });

        app.MapPost("/api/groups", (HttpContext ctx, GroupUpsertRequest request, ActivityStore activity, DashboardQueryService query) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var name = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return BadRequest("Не задано имя группы");

            try
            {
                var id = activity.CreateGroup(name, request.Description?.Trim(), request.Color?.Trim());
                if (request.AppIds is { Count: > 0 }) activity.SetGroupMembers(id, request.AppIds);
                var created = query.BuildGroups(TimeUtil.TodayKey()).FirstOrDefault(g => g.Id == id);
                AppLog.Info($"Создана группа приложений «{name}» (#{id})");
                return created is null ? BadRequest("Группа создана, но не найдена") : Results.Json(created);
            }
            catch (SqliteException)
            {
                return BadRequest($"Группа с именем «{name}» уже существует");
            }
        });

        app.MapPut("/api/groups/{id:long}", (HttpContext ctx, long id, GroupUpsertRequest request, ActivityStore activity, DashboardQueryService query) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var existing = activity.GetGroup(id);
            if (existing is null) return NotFound($"Группа #{id} не найдена");

            var name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim();
            try
            {
                activity.UpdateGroup(id,
                    name,
                    request.Description is null ? existing.Description : request.Description.Trim(),
                    request.Color is null ? existing.Color : request.Color.Trim());
                if (request.AppIds is not null) activity.SetGroupMembers(id, request.AppIds);
            }
            catch (SqliteException)
            {
                return BadRequest($"Группа с именем «{name}» уже существует");
            }

            var updated = query.BuildGroups(TimeUtil.TodayKey()).FirstOrDefault(g => g.Id == id);
            return updated is null ? NotFound($"Группа #{id} не найдена") : Results.Json(updated);
        });

        app.MapDelete("/api/groups/{id:long}", (HttpContext ctx, long id, ActivityStore activity) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            if (activity.GetGroup(id) is null) return NotFound($"Группа #{id} не найдена");
            activity.DeleteGroup(id);
            AppLog.Info($"Удалена группа приложений #{id}");
            return Results.Json(new { ok = true });
        });
    }

    // =============================================================== критическое время

    private static void MapCritical(WebApplication app, AppActiveOptions options)
    {
        app.MapGet("/api/critical-rules", (HttpContext ctx, DashboardQueryService query) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;
            return Results.Json(new { rules = query.BuildCriticalRules() });
        });

        app.MapPost("/api/critical-rules", (HttpContext ctx, CriticalRuleUpsertRequest request, RulesStore rules, DashboardQueryService query) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var validation = ValidateCriticalRule(request, null);
            if (validation is not null) return validation;

            var record = ToRuleRecord(request, null);
            record.Id = rules.CreateCriticalRule(record);
            AppLog.Info($"Создано правило критического времени «{record.Name}» (#{record.Id}), лимит {record.DailyLimitSeconds:0} с/сутки");

            var created = query.BuildCriticalRules().FirstOrDefault(r => r.Id == record.Id);
            return created is null ? BadRequest("Правило создано, но не найдено") : Results.Json(created);
        });

        app.MapPut("/api/critical-rules/{id:long}", (HttpContext ctx, long id, CriticalRuleUpsertRequest request, RulesStore rules, DashboardQueryService query) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var existing = rules.GetCriticalRule(id);
            if (existing is null) return NotFound($"Правило #{id} не найдено");

            var validation = ValidateCriticalRule(request, existing);
            if (validation is not null) return validation;

            var record = ToRuleRecord(request, existing);
            rules.UpdateCriticalRule(record);

            var updated = query.BuildCriticalRules().FirstOrDefault(r => r.Id == id);
            return updated is null ? NotFound($"Правило #{id} не найдено") : Results.Json(updated);
        });

        app.MapDelete("/api/critical-rules/{id:long}", (HttpContext ctx, long id, RulesStore rules) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            if (rules.GetCriticalRule(id) is null) return NotFound($"Правило #{id} не найдено");
            rules.DeleteCriticalRule(id);
            AppLog.Info($"Удалено правило критического времени #{id}");
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/critical/status", (HttpContext ctx, string? date, DashboardQueryService query) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            var dayKey = ResolveDate(date);
            return Results.Json(new { date = dayKey, rules = query.BuildCriticalStatuses(dayKey) });
        });
    }

    private static IResult? ValidateCriticalRule(CriticalRuleUpsertRequest request, CriticalRuleRecord? existing)
    {
        var name = request.Name?.Trim() ?? existing?.Name;
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("Не задано имя правила");

        var targetType = (request.TargetType ?? existing?.TargetType ?? "app").Trim().ToLowerInvariant();
        if (targetType is not ("app" or "group")) return BadRequest("targetType должен быть 'app' или 'group'");

        var limit = request.DailyLimitSeconds ?? existing?.DailyLimitSeconds ?? 0;
        if (limit <= 0) return BadRequest("dailyLimitSeconds должен быть больше нуля");

        var severity = (request.Severity ?? existing?.Severity ?? "warning").Trim().ToLowerInvariant();
        if (!AllowedSeverities.Contains(severity)) return BadRequest($"severity должно быть одним из: {string.Join(", ", AllowedSeverities)}");

        return null;
    }

    private static CriticalRuleRecord ToRuleRecord(CriticalRuleUpsertRequest request, CriticalRuleRecord? existing)
    {
        var targetType = (request.TargetType ?? existing?.TargetType ?? "app").Trim().ToLowerInvariant();
        var targetId = request.TargetId ?? (targetType == "group" ? existing?.GroupId : existing?.ApplicationId) ?? 0;
        var days = request.DaysOfWeek is null
            ? existing?.DaysOfWeek
            : (request.DaysOfWeek.Count == 0
                ? null
                : string.Join(',', request.DaysOfWeek.Where(d => d is >= 1 and <= 7).Distinct().OrderBy(d => d)));

        return new CriticalRuleRecord
        {
            Id = existing?.Id ?? 0,
            Name = (request.Name ?? existing?.Name ?? "").Trim(),
            TargetType = targetType,
            ApplicationId = targetType == "app" ? targetId : null,
            GroupId = targetType == "group" ? targetId : null,
            DailyLimitSeconds = request.DailyLimitSeconds ?? existing?.DailyLimitSeconds ?? 0,
            Enabled = request.Enabled ?? existing?.Enabled ?? true,
            Severity = (request.Severity ?? existing?.Severity ?? "warning").Trim().ToLowerInvariant(),
            DaysOfWeek = days,
            CreatedUtc = existing?.CreatedUtc ?? TimeUtil.ToIso(DateTime.UtcNow),
            UpdatedUtc = TimeUtil.ToIso(DateTime.UtcNow)
        };
    }

    // =============================================================== workflow

    private static void MapWorkflow(WebApplication app, AppActiveOptions options)
    {
        app.MapGet("/api/workflow/rules", (HttpContext ctx, DashboardQueryService query) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;
            return Results.Json(new { rules = query.BuildWorkflowRules() });
        });

        app.MapPost("/api/workflow/rules", (HttpContext ctx, WorkflowRuleUpsertRequest request, RulesStore rules, DashboardQueryService query) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var validation = ValidateWorkflowRule(request, null);
            if (validation is not null) return validation;

            var record = ToWorkflowRecord(request, null);
            record.Id = rules.CreateWorkflowRule(record);
            AppLog.Info($"Создано Workflow-правило «{record.Name}» (#{record.Id}), действие: {record.ActionType}");

            var created = query.BuildWorkflowRules().FirstOrDefault(r => r.Id == record.Id);
            return created is null ? BadRequest("Правило создано, но не найдено") : Results.Json(created);
        });

        app.MapPut("/api/workflow/rules/{id:long}", (HttpContext ctx, long id, WorkflowRuleUpsertRequest request, RulesStore rules, DashboardQueryService query) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var existing = rules.GetWorkflowRule(id);
            if (existing is null) return NotFound($"Workflow-правило #{id} не найдено");

            var validation = ValidateWorkflowRule(request, existing);
            if (validation is not null) return validation;

            var record = ToWorkflowRecord(request, existing);
            rules.UpdateWorkflowRule(record);

            var updated = query.BuildWorkflowRules().FirstOrDefault(r => r.Id == id);
            return updated is null ? NotFound($"Workflow-правило #{id} не найдено") : Results.Json(updated);
        });

        app.MapDelete("/api/workflow/rules/{id:long}", (HttpContext ctx, long id, RulesStore rules) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            if (rules.GetWorkflowRule(id) is null) return NotFound($"Workflow-правило #{id} не найдено");
            rules.DeleteWorkflowRule(id);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/workflow/events", (HttpContext ctx, string? date, int? limit, DashboardQueryService query) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            var dayKey = ResolveDate(date);
            var take = Math.Clamp(limit ?? 100, 1, 1000);
            return Results.Json(new { date = dayKey, events = query.BuildEvents(dayKey, take) });
        });

        app.MapPost("/api/workflow/run", (HttpContext ctx, WorkflowRunRequest? request, WorkflowEngine engine) =>
        {
            var guard = Guard.Write(ctx, options);
            if (guard is not null) return guard;

            var dayKey = ResolveDate(request?.Date);
            var result = engine.Run(dayKey, force: true);
            AppLog.Info($"Workflow запущен вручную за {dayKey}: событий {result.EventsCreated}, действий {result.Actions.Count}");

            return Results.Json(new
            {
                ok = true,
                date = result.Date,
                evaluatedAt = result.EvaluatedAt,
                eventsCreated = result.EventsCreated,
                actions = result.Actions.Select(a => new
                {
                    ruleName = a.RuleName,
                    actionType = a.ActionType,
                    status = a.Status,
                    message = a.Message
                })
            });
        });
    }

    private static IResult? ValidateWorkflowRule(WorkflowRuleUpsertRequest request, WorkflowRuleRecord? existing)
    {
        var name = request.Name?.Trim() ?? existing?.Name;
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("Не задано имя правила");

        var trigger = (request.TriggerType ?? existing?.TriggerType ?? "criticalExceeded").Trim();
        if (trigger is not ("criticalExceeded" or "schedule"))
            return BadRequest("triggerType должен быть 'criticalExceeded' или 'schedule'");

        var action = (request.ActionType ?? existing?.ActionType ?? "event").Trim().ToLowerInvariant();
        if (action is not ("event" or "script" or "webhook"))
            return BadRequest("actionType должен быть 'event', 'script' или 'webhook'");

        if (trigger == "criticalExceeded")
        {
            var criticalRuleId = request.CriticalRuleId ?? existing?.CriticalRuleId;
            if (criticalRuleId is null or <= 0) return BadRequest("Для triggerType='criticalExceeded' требуется criticalRuleId");
        }

        if (action is "script" or "webhook" && string.IsNullOrWhiteSpace(request.ActionTarget ?? existing?.ActionTarget))
            return BadRequest($"Для actionType='{action}' требуется actionTarget");

        return null;
    }

    private static WorkflowRuleRecord ToWorkflowRecord(WorkflowRuleUpsertRequest request, WorkflowRuleRecord? existing) => new()
    {
        Id = existing?.Id ?? 0,
        Name = (request.Name ?? existing?.Name ?? "").Trim(),
        Enabled = request.Enabled ?? existing?.Enabled ?? true,
        TriggerType = (request.TriggerType ?? existing?.TriggerType ?? "criticalExceeded").Trim(),
        CriticalRuleId = request.CriticalRuleId ?? existing?.CriticalRuleId,
        ScheduleMinutes = request.ScheduleMinutes ?? existing?.ScheduleMinutes ?? 5,
        TargetType = request.TargetType ?? existing?.TargetType,
        TargetId = request.TargetId ?? existing?.TargetId,
        ThresholdSeconds = request.ThresholdSeconds ?? existing?.ThresholdSeconds,
        ActionType = (request.ActionType ?? existing?.ActionType ?? "event").Trim().ToLowerInvariant(),
        ActionTarget = request.ActionTarget ?? existing?.ActionTarget,
        CooldownMinutes = request.CooldownMinutes ?? existing?.CooldownMinutes ?? 60,
        CreatedUtc = existing?.CreatedUtc ?? TimeUtil.ToIso(DateTime.UtcNow),
        UpdatedUtc = TimeUtil.ToIso(DateTime.UtcNow)
    };

    // =============================================================== dashboard

    private static void MapDashboard(WebApplication app, AppActiveOptions options)
    {
        app.MapGet("/api/dashboard", (HttpContext ctx, string? date, string? from, string? to, DashboardQueryService query) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            var range = ResolveRange(from, to, out var rangeError);
            if (rangeError is not null) return BadRequest(rangeError);

            return Results.Json(query.BuildDashboard(ResolveDate(date, range), range));
        });

        app.MapGet("/api/range/resolve", (HttpContext ctx, string? from, string? to) =>
        {
            var range = ResolveRange(from, to, out var rangeError);
            if (rangeError is not null)
            {
                return BadRequest(rangeError);
            }

            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            return Results.Json(new
            {
                ok = true,
                fromUtc = TimeUtil.ToIso(range!.FromUtc),
                toUtc = TimeUtil.ToIso(range.ToUtc),
                fromLocal = TimeUtil.ToLocal(range.FromUtc).ToString("yyyy-MM-dd HH:mm:ss"),
                toLocal = TimeUtil.ToLocal(range.ToUtc).ToString("yyyy-MM-dd HH:mm:ss"),
                label = range.Label,
                totalSeconds = Math.Round(range.TotalSeconds, 0),
                bucketKind = range.TotalSeconds <= 48 * 3600 ? "hour" : "day"
            });
        });

        app.MapGet("/api/export/csv", (HttpContext ctx, string? date, string? from, string? to, ActivityStore activity) =>
        {
            var guard = Guard.Read(ctx, options);
            if (guard is not null) return guard;

            var range = ResolveRange(from, to, out var rangeError);
            if (rangeError is not null) return BadRequest(rangeError);

            var dayKey = ResolveDate(date, range);
            var apps = activity.ListApplications().ToDictionary(a => a.Id);
            var builder = new StringBuilder();
            builder.Append('\uFEFF');
            builder.AppendLine("Application;Path;User;Session;Start;End;Seconds;Hour");

            if (range is not null)
            {
                foreach (var row in activity.GetIntervalsInRange(range.FromUtc, range.ToUtc))
                {
                    var seconds = row.SecondsInRange(range.FromUtc, range.ToUtc);
                    if (seconds <= 0.01) continue;
                    apps.TryGetValue(row.AppId, out var rowApp);
                    var start = TimeUtil.ToLocal(row.StartUtc > range.FromUtc ? row.StartUtc : range.FromUtc);
                    var end = TimeUtil.ToLocal(row.EndUtc < range.ToUtc ? row.EndUtc : range.ToUtc);
                    builder.Append(Escape(rowApp?.DisplayName ?? row.DisplayName)).Append(';')
                        .Append(Escape(rowApp?.ExePath ?? "")).Append(';')
                        .Append(Escape(row.UserName)).Append(';')
                        .Append(row.SessionId).Append(';')
                        .Append(start.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                        .Append(end.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                        .Append(Math.Round(seconds, 1)).Append(';')
                        .Append(row.HourIndex)
                        .AppendLine();
                }

                ctx.Response.Headers.ContentDisposition =
                    $"attachment; filename=\"activity-{TimeUtil.ToLocal(range.FromUtc):yyyyMMdd-HHmm}-{TimeUtil.ToLocal(range.ToUtc):yyyyMMdd-HHmm}.csv\"";
                return Results.Text(builder.ToString(), "text/csv", Encoding.UTF8);
            }

            var intervals = activity.GetDayIntervals(dayKey, 200000);
            foreach (var interval in intervals)
            {
                apps.TryGetValue(interval.AppId, out var app);
                var start = TimeUtil.ToLocal(TimeUtil.ParseIso(interval.StartUtc));
                var end = TimeUtil.ToLocal(TimeUtil.ParseIso(interval.EndUtc));
                builder.Append(Escape(app?.DisplayName ?? interval.DisplayName)).Append(';')
                    .Append(Escape(app?.ExePath ?? "")).Append(';')
                    .Append(Escape(interval.UserName)).Append(';')
                    .Append(interval.SessionId).Append(';')
                    .Append(start.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                    .Append(end.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                    .Append(Math.Round(interval.Seconds, 1)).Append(';')
                    .Append(interval.HourIndex)
                    .AppendLine();
            }

            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"activity-{dayKey}.csv\"";
            return Results.Text(builder.ToString(), "text/csv", Encoding.UTF8);
        });
    }

    private static string Escape(string value) => value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');

    // =============================================================== ingest

    private static void MapIngest(WebApplication app, AppActiveOptions options)
    {
        app.MapPost("/api/ingest/report", (HttpContext ctx, IngestReportRequest request, AgentRegistry registry,
            IngestCounters counters, SettingsService settings, ActivityStore activity, ActivityAccumulator accumulator) =>
        {
            if (!Guard.CheckIngestKey(ctx, options))
            {
                return Results.Json(new { ok = false, error = "Неверный ключ агента" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var now = DateTime.UtcNow;
            var current = settings.Current;

            registry.RecordReport(new AgentReportSnapshot(
                request.SessionId,
                request.UserName,
                request.Domain,
                request.Pid,
                request.AgentVersion,
                request.CpuPercent,
                request.WorkingSetMb,
                request.Errors ?? new List<string>()));

            if (request.SessionId > 0)
            {
                activity.UpsertSession(request.SessionId, request.UserName, request.Domain, "Active", now);
            }

            var accepted = 0;
            var skipped = 0;
            double seconds = 0;

            if (current.MonitorEnabled && request.Samples is { Count: > 0 })
            {
                foreach (var sample in request.Samples)
                {
                    if (string.IsNullOrWhiteSpace(sample.ExeName) || sample.Seconds <= 0 || sample.Seconds > 3600)
                    {
                        skipped++;
                        continue;
                    }

                    if (current.IsIgnored(sample.ExeName))
                    {
                        skipped++;
                        continue;
                    }

                    var appKey = string.IsNullOrWhiteSpace(sample.AppKey)
                        ? ActivityStore.BuildAppKey(sample.ExeName, sample.ExePath)
                        : sample.AppKey;

                    var app = activity.EnsureApplication(appKey, sample.ExeName, sample.ExePath, now);
                    seconds += accumulator.Add(app.Id, request.SessionId, request.UserName, sample.StartUtc,
                        sample.Seconds, sample.WindowTitle, now);
                    accepted++;
                }
            }

            counters.AddReport(accepted, seconds, skipped > 0, request.Errors?.Count ?? 0);

            return Results.Json(new
            {
                ok = true,
                serverTimeUtc = TimeUtil.ToIso(now),
                acceptedSamples = accepted,
                acceptedSeconds = Math.Round(seconds, 2),
                skippedSamples = skipped,
                config = AgentConfigDto(current)
            });
        });

        app.MapGet("/api/ingest/config", (HttpContext ctx, SettingsService settings) =>
        {
            if (!Guard.CheckIngestKey(ctx, options))
            {
                return Results.Json(new { ok = false, error = "Неверный ключ агента" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Json(new
            {
                ok = true,
                serverTimeUtc = TimeUtil.ToIso(DateTime.UtcNow),
                config = AgentConfigDto(settings.Current)
            });
        });
    }

    private static object AgentConfigDto(AppSettings settings) => new
    {
        pollIntervalSeconds = settings.PollIntervalSeconds,
        activityIdleSeconds = settings.ActivityIdleSeconds,
        monitorEnabled = settings.MonitorEnabled,
        collectWindowTitles = settings.CollectWindowTitles,
        heartbeatSeconds = settings.AgentHeartbeatSeconds
    };

    // =============================================================== помощники

    private static string ResolveDate(string? date) => TimeUtil.IsValidDayKey(date) ? date! : TimeUtil.TodayKey();

    /// <summary>Сутки, к которым относится запрос: явная дата, последние сутки периода или сегодня.</summary>
    private static string ResolveDate(string? date, TimeRange? range)
    {
        if (TimeUtil.IsValidDayKey(date)) return date!;
        if (range is not null) return TimeUtil.DayKey(range.ToUtc.AddTicks(-1));
        return TimeUtil.TodayKey();
    }

    /// <summary>
    /// Разбирает период из параметров from/to (поддерживается формат Grafana: now-1d, now+1h).
    /// Если оба параметра пусты — периода нет, запрос считается по суткам.
    /// </summary>
    private static TimeRange? ResolveRange(string? from, string? to, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to)) return null;

        if (!TimeRangeParser.TryParse(from, to, null, out var range, out var parseError))
        {
            error = parseError;
            return null;
        }

        return range;
    }

    private static IResult BadRequest(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status404NotFound);
}
