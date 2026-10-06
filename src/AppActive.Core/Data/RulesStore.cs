using AppActive.Core.Models;
using Microsoft.Data.Sqlite;

namespace AppActive.Core.Data;

/// <summary>Правила критического времени, правила Workflow и журнал событий.</summary>
public sealed class RulesStore
{
    private readonly SqliteDatabase _db;

    public RulesStore(SqliteDatabase database) => _db = database;

    // ---------------------------------------------------------------- критическое время

    private const string CriticalSelect =
        """
        SELECT Id, Name, TargetType, ApplicationId, GroupId, DailyLimitSeconds, Enabled, Severity, DaysOfWeek, CreatedUtc, UpdatedUtc
          FROM CriticalRules
        """;

    public List<CriticalRuleRecord> ListCriticalRules()
    {
        using var cn = _db.Open();
        return Db.Query(cn, CriticalSelect + " ORDER BY Id;", MapCritical);
    }

    public CriticalRuleRecord? GetCriticalRule(long id)
    {
        using var cn = _db.Open();
        return Db.Query(cn, CriticalSelect + " WHERE Id=@id;", MapCritical, ("@id", id)).FirstOrDefault();
    }

    public long CreateCriticalRule(CriticalRuleRecord rule)
    {
        using var cn = _db.Open();
        var now = TimeUtil.ToIso(DateTime.UtcNow);
        Db.Exec(cn,
            """
            INSERT INTO CriticalRules(Name, TargetType, ApplicationId, GroupId, DailyLimitSeconds, Enabled, Severity, DaysOfWeek, CreatedUtc, UpdatedUtc)
            VALUES(@name, @target, @app, @group, @limit, @enabled, @sev, @days, @now, @now);
            """,
            ("@name", rule.Name), ("@target", rule.TargetType), ("@app", rule.ApplicationId), ("@group", rule.GroupId),
            ("@limit", rule.DailyLimitSeconds), ("@enabled", rule.Enabled ? 1 : 0), ("@sev", rule.Severity),
            ("@days", rule.DaysOfWeek), ("@now", now));
        return Db.LastRowId(cn, null);
    }

    public void UpdateCriticalRule(CriticalRuleRecord rule)
    {
        using var cn = _db.Open();
        Db.Exec(cn,
            """
            UPDATE CriticalRules
               SET Name=@name, TargetType=@target, ApplicationId=@app, GroupId=@group, DailyLimitSeconds=@limit,
                   Enabled=@enabled, Severity=@sev, DaysOfWeek=@days, UpdatedUtc=@now
             WHERE Id=@id;
            """,
            ("@name", rule.Name), ("@target", rule.TargetType), ("@app", rule.ApplicationId), ("@group", rule.GroupId),
            ("@limit", rule.DailyLimitSeconds), ("@enabled", rule.Enabled ? 1 : 0), ("@sev", rule.Severity),
            ("@days", rule.DaysOfWeek), ("@now", TimeUtil.ToIso(DateTime.UtcNow)), ("@id", rule.Id));
    }

    public void DeleteCriticalRule(long id)
    {
        using var cn = _db.Open();
        Db.Exec(cn, "DELETE FROM CriticalRules WHERE Id=@id;", ("@id", id));
    }

    private static CriticalRuleRecord MapCritical(SqliteDataReader r) => new()
    {
        Id = Db.Lng(r, 0),
        Name = Db.Str(r, 1),
        TargetType = Db.Str(r, 2),
        ApplicationId = Db.LngOrNull(r, 3),
        GroupId = Db.LngOrNull(r, 4),
        DailyLimitSeconds = Db.Dbl(r, 5),
        Enabled = Db.Bool(r, 6),
        Severity = Db.Str(r, 7),
        DaysOfWeek = Db.StrOrNull(r, 8),
        CreatedUtc = Db.Str(r, 9),
        UpdatedUtc = Db.Str(r, 10)
    };

    // ---------------------------------------------------------------- workflow

    private const string WorkflowSelect =
        """
        SELECT Id, Name, Enabled, TriggerType, CriticalRuleId, ScheduleMinutes, TargetType, TargetId, ThresholdSeconds,
               ActionType, ActionTarget, CooldownMinutes, LastRunUtc, LastStatus, TriggerCount, CreatedUtc, UpdatedUtc
          FROM WorkflowRules
        """;

    public List<WorkflowRuleRecord> ListWorkflowRules()
    {
        using var cn = _db.Open();
        return Db.Query(cn, WorkflowSelect + " ORDER BY Id;", MapWorkflow);
    }

    public WorkflowRuleRecord? GetWorkflowRule(long id)
    {
        using var cn = _db.Open();
        return Db.Query(cn, WorkflowSelect + " WHERE Id=@id;", MapWorkflow, ("@id", id)).FirstOrDefault();
    }

    public long CreateWorkflowRule(WorkflowRuleRecord rule)
    {
        using var cn = _db.Open();
        var now = TimeUtil.ToIso(DateTime.UtcNow);
        Db.Exec(cn,
            """
            INSERT INTO WorkflowRules(Name, Enabled, TriggerType, CriticalRuleId, ScheduleMinutes, TargetType, TargetId,
                                      ThresholdSeconds, ActionType, ActionTarget, CooldownMinutes, TriggerCount, CreatedUtc, UpdatedUtc)
            VALUES(@name, @enabled, @trigger, @criticalId, @schedule, @targetType, @targetId, @threshold, @action, @target, @cooldown, 0, @now, @now);
            """,
            ("@name", rule.Name), ("@enabled", rule.Enabled ? 1 : 0), ("@trigger", rule.TriggerType),
            ("@criticalId", rule.CriticalRuleId), ("@schedule", rule.ScheduleMinutes), ("@targetType", rule.TargetType),
            ("@targetId", rule.TargetId), ("@threshold", rule.ThresholdSeconds), ("@action", rule.ActionType),
            ("@target", rule.ActionTarget), ("@cooldown", rule.CooldownMinutes), ("@now", now));
        return Db.LastRowId(cn, null);
    }

    public void UpdateWorkflowRule(WorkflowRuleRecord rule)
    {
        using var cn = _db.Open();
        Db.Exec(cn,
            """
            UPDATE WorkflowRules
               SET Name=@name, Enabled=@enabled, TriggerType=@trigger, CriticalRuleId=@criticalId, ScheduleMinutes=@schedule,
                   TargetType=@targetType, TargetId=@targetId, ThresholdSeconds=@threshold, ActionType=@action,
                   ActionTarget=@target, CooldownMinutes=@cooldown, UpdatedUtc=@now
             WHERE Id=@id;
            """,
            ("@name", rule.Name), ("@enabled", rule.Enabled ? 1 : 0), ("@trigger", rule.TriggerType),
            ("@criticalId", rule.CriticalRuleId), ("@schedule", rule.ScheduleMinutes), ("@targetType", rule.TargetType),
            ("@targetId", rule.TargetId), ("@threshold", rule.ThresholdSeconds), ("@action", rule.ActionType),
            ("@target", rule.ActionTarget), ("@cooldown", rule.CooldownMinutes),
            ("@now", TimeUtil.ToIso(DateTime.UtcNow)), ("@id", rule.Id));
    }

    public void DeleteWorkflowRule(long id)
    {
        using var cn = _db.Open();
        Db.Exec(cn, "DELETE FROM WorkflowRules WHERE Id=@id;", ("@id", id));
    }

    public void MarkWorkflowRun(long id, string status)
    {
        using var cn = _db.Open();
        Db.Exec(cn,
            "UPDATE WorkflowRules SET LastRunUtc=@now, LastStatus=@status, TriggerCount=TriggerCount+1 WHERE Id=@id;",
            ("@now", TimeUtil.ToIso(DateTime.UtcNow)), ("@status", status), ("@id", id));
    }

    public void MarkWorkflowChecked(long id)
    {
        using var cn = _db.Open();
        Db.Exec(cn, "UPDATE WorkflowRules SET LastRunUtc=@now WHERE Id=@id;",
            ("@now", TimeUtil.ToIso(DateTime.UtcNow)), ("@id", id));
    }

    private static WorkflowRuleRecord MapWorkflow(SqliteDataReader r) => new()
    {
        Id = Db.Lng(r, 0),
        Name = Db.Str(r, 1),
        Enabled = Db.Bool(r, 2),
        TriggerType = Db.Str(r, 3),
        CriticalRuleId = Db.LngOrNull(r, 4),
        ScheduleMinutes = Db.Int(r, 5),
        TargetType = Db.StrOrNull(r, 6),
        TargetId = Db.LngOrNull(r, 7),
        ThresholdSeconds = Db.DblOrNull(r, 8),
        ActionType = Db.Str(r, 9),
        ActionTarget = Db.StrOrNull(r, 10),
        CooldownMinutes = Db.Int(r, 11),
        LastRunUtc = Db.StrOrNull(r, 12),
        LastStatus = Db.StrOrNull(r, 13),
        TriggerCount = Db.Lng(r, 14),
        CreatedUtc = Db.Str(r, 15),
        UpdatedUtc = Db.Str(r, 16)
    };

    // ---------------------------------------------------------------- события

    public long InsertEvent(WorkflowEventRecord e)
    {
        using var cn = _db.Open();
        Db.Exec(cn,
            """
            INSERT INTO WorkflowEvents(OccurredUtc, DayKey, SourceType, SourceId, RuleName, Severity, Message,
                                       TargetName, UsedSeconds, LimitSeconds, ActionType, ActionStatus, DedupKey)
            VALUES(@occ, @day, @srcType, @srcId, @rule, @sev, @msg, @target, @used, @limit, @action, @status, @dedup);
            """,
            ("@occ", e.OccurredUtc), ("@day", e.DayKey), ("@srcType", e.SourceType), ("@srcId", e.SourceId),
            ("@rule", e.RuleName), ("@sev", e.Severity), ("@msg", e.Message), ("@target", e.TargetName),
            ("@used", e.UsedSeconds), ("@limit", e.LimitSeconds), ("@action", e.ActionType),
            ("@status", e.ActionStatus), ("@dedup", e.DedupKey));
        return Db.LastRowId(cn, null);
    }

    public bool EventExists(string sourceType, long? sourceId, string dedupKey)
    {
        using var cn = _db.Open();
        var count = Db.Long(cn,
            "SELECT COUNT(1) FROM WorkflowEvents WHERE SourceType=@t AND IFNULL(SourceId, -1)=@id AND DedupKey=@dedup;",
            ("@t", sourceType), ("@id", sourceId ?? -1), ("@dedup", dedupKey));
        return count > 0;
    }

    public string? FirstEventUtc(string sourceType, long? sourceId, string dayKey)
    {
        using var cn = _db.Open();
        return Db.Query(cn,
            "SELECT OccurredUtc FROM WorkflowEvents WHERE SourceType=@t AND IFNULL(SourceId, -1)=@id AND DayKey=@day ORDER BY OccurredUtc LIMIT 1;",
            r => Db.Str(r, 0),
            ("@t", sourceType), ("@id", sourceId ?? -1), ("@day", dayKey)).FirstOrDefault();
    }

    public List<WorkflowEventRecord> ListEvents(string dayKey, int limit = 200)
    {
        using var cn = _db.Open();
        return Db.Query(cn,
            """
            SELECT Id, OccurredUtc, DayKey, SourceType, SourceId, RuleName, Severity, Message, TargetName,
                   UsedSeconds, LimitSeconds, ActionType, ActionStatus, DedupKey
              FROM WorkflowEvents
             WHERE DayKey=@day
             ORDER BY OccurredUtc DESC
             LIMIT @limit;
            """,
            r => new WorkflowEventRecord
            {
                Id = Db.Lng(r, 0),
                OccurredUtc = Db.Str(r, 1),
                DayKey = Db.Str(r, 2),
                SourceType = Db.Str(r, 3),
                SourceId = Db.LngOrNull(r, 4),
                RuleName = Db.Str(r, 5),
                Severity = Db.Str(r, 6),
                Message = Db.Str(r, 7),
                TargetName = Db.StrOrNull(r, 8),
                UsedSeconds = Db.DblOrNull(r, 9),
                LimitSeconds = Db.DblOrNull(r, 10),
                ActionType = Db.StrOrNull(r, 11),
                ActionStatus = Db.StrOrNull(r, 12),
                DedupKey = Db.StrOrNull(r, 13)
            },
            ("@day", dayKey), ("@limit", limit));
    }

    public int DeleteEventsBefore(string dayKeyExclusive)
    {
        using var cn = _db.Open();
        return Db.Exec(cn, "DELETE FROM WorkflowEvents WHERE DayKey < @day;", ("@day", dayKeyExclusive));
    }
}
