using System.Collections.Concurrent;
using AppActive.Core.Models;
using Microsoft.Data.Sqlite;

namespace AppActive.Core.Data;

public sealed record AppUsageRow(long AppId, int SessionId, string UserName, double Seconds);

/// <summary>Репозиторий приложений, интервалов активности, групп и сеансов.</summary>
public sealed class ActivityStore
{
    private const int TouchThrottleSeconds = 60;

    private readonly SqliteDatabase _db;
    private readonly ConcurrentDictionary<string, CachedApp> _appCache = new(StringComparer.OrdinalIgnoreCase);

    public ActivityStore(SqliteDatabase database) => _db = database;

    public SqliteDatabase Database => _db;

    public string DatabasePath => _db.Path;

    public SqliteConnection Open() => _db.Open();

    private sealed class CachedApp
    {
        public ApplicationRecord Record { get; init; } = new();
        public DateTime LastTouchUtc { get; set; }
    }

    public static string BuildAppKey(string? exeName, string? exePath) =>
        (exeName ?? "").Trim().ToLowerInvariant() + "|" + (exePath ?? "").Trim().ToLowerInvariant();

    // ---------------------------------------------------------------- приложения

    /// <summary>
    /// Возвращает приложение по ключу, создавая запись, если его ещё нет в базе.
    /// Кэш в памяти избавляет от SELECT на каждое событие.
    /// </summary>
    public ApplicationRecord EnsureApplication(string appKey, string exeName, string? exePath, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(appKey)) appKey = BuildAppKey(exeName, exePath);
        var nowIso = TimeUtil.ToIso(nowUtc);

        if (_appCache.TryGetValue(appKey, out var cached))
        {
            if ((nowUtc - cached.LastTouchUtc).TotalSeconds >= TouchThrottleSeconds)
            {
                using var cnTouch = _db.Open();
                Db.Exec(cnTouch, "UPDATE Applications SET LastSeenUtc=@now WHERE Id=@id",
                    ("@now", nowIso), ("@id", cached.Record.Id));
                cached.LastTouchUtc = nowUtc;
            }
            return cached.Record;
        }

        using var cn = _db.Open();
        ApplicationRecord? record = null;
        using (var cmd = Db.Cmd(cn, null, "SELECT Id, AppKey, ExeName, ExePath, FriendlyName, Category, IsIgnored, FirstSeenUtc, LastSeenUtc FROM Applications WHERE AppKey=@key LIMIT 1;", new (string, object?)[] { ("@key", appKey) }))
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read()) record = MapApplication(r);
        }

        if (record is null)
        {
            try
            {
                Db.Exec(cn,
                    "INSERT INTO Applications(AppKey, ExeName, ExePath, FirstSeenUtc, LastSeenUtc) VALUES(@key, @exe, @path, @now, @now);",
                    ("@key", appKey), ("@exe", exeName), ("@path", exePath), ("@now", nowIso));
                var id = Db.LastRowId(cn, null);
                record = new ApplicationRecord
                {
                    Id = id,
                    AppKey = appKey,
                    ExeName = exeName,
                    ExePath = exePath,
                    FirstSeenUtc = nowIso,
                    LastSeenUtc = nowIso
                };
            }
            catch (SqliteException)
            {
                // Гонка двух сеансов на первом появлении приложения — перечитываем.
                using var cmd = Db.Cmd(cn, null, "SELECT Id, AppKey, ExeName, ExePath, FriendlyName, Category, IsIgnored, FirstSeenUtc, LastSeenUtc FROM Applications WHERE AppKey=@key LIMIT 1;", new (string, object?)[] { ("@key", appKey) });
                using var r = cmd.ExecuteReader();
                if (r.Read()) record = MapApplication(r);
            }
        }

        if (record is null) throw new InvalidOperationException($"Не удалось получить приложение '{appKey}'");

        _appCache[appKey] = new CachedApp { Record = record, LastTouchUtc = nowUtc };
        return record;
    }

    public ApplicationRecord? GetApplication(long id)
    {
        using var cn = _db.Open();
        return Db.Query(cn, "SELECT Id, AppKey, ExeName, ExePath, FriendlyName, Category, IsIgnored, FirstSeenUtc, LastSeenUtc FROM Applications WHERE Id=@id;",
            MapApplication, ("@id", id)).FirstOrDefault();
    }

    public List<ApplicationRecord> ListApplications()
    {
        using var cn = _db.Open();
        return Db.Query(cn, "SELECT Id, AppKey, ExeName, ExePath, FriendlyName, Category, IsIgnored, FirstSeenUtc, LastSeenUtc FROM Applications ORDER BY ExeName COLLATE NOCASE;",
            MapApplication);
    }

    public void UpdateApplication(long id, string? friendlyName, bool setFriendlyName, string? category, bool setCategory, bool? isIgnored)
    {
        using var cn = _db.Open();
        if (setFriendlyName)
        {
            Db.Exec(cn, "UPDATE Applications SET FriendlyName=@v WHERE Id=@id",
                ("@v", string.IsNullOrWhiteSpace(friendlyName) ? null : friendlyName.Trim()), ("@id", id));
        }
        if (setCategory)
        {
            Db.Exec(cn, "UPDATE Applications SET Category=@v WHERE Id=@id",
                ("@v", string.IsNullOrWhiteSpace(category) ? null : category.Trim()), ("@id", id));
        }
        if (isIgnored.HasValue)
        {
            Db.Exec(cn, "UPDATE Applications SET IsIgnored=@v WHERE Id=@id", ("@v", isIgnored.Value ? 1 : 0), ("@id", id));
        }
        _appCache.Clear();
    }

    public int CountApplications() => (int)Db.Long(_db.Open(), "SELECT COUNT(1) FROM Applications;");

    private static ApplicationRecord MapApplication(SqliteDataReader r) => new()
    {
        Id = Db.Lng(r, 0),
        AppKey = Db.Str(r, 1),
        ExeName = Db.Str(r, 2),
        ExePath = Db.StrOrNull(r, 3),
        FriendlyName = Db.StrOrNull(r, 4),
        Category = Db.StrOrNull(r, 5),
        IsIgnored = Db.Bool(r, 6),
        FirstSeenUtc = Db.Str(r, 7),
        LastSeenUtc = Db.Str(r, 8)
    };

    // ---------------------------------------------------------------- интервалы

    /// <summary>Ищет «открытый» интервал, который можно продлить (в пределах часа и без большого разрыва).</summary>
    public long? FindOpenIntervalRow(SqliteConnection cn, SqliteTransaction? tx, long appId, int sessionId, string userName,
        string dayKey, int hourIndex, DateTime minEndUtc)
    {
        var rows = Db.Query(cn, tx,
            """
            SELECT Id FROM ActivityIntervals
             WHERE ApplicationId=@app AND SessionId=@sess AND UserName=@user AND DayKey=@day AND HourIndex=@hour AND EndUtc >= @minEnd
             ORDER BY EndUtc DESC LIMIT 1;
            """,
            r => Db.Lng(r, 0),
            ("@app", appId), ("@sess", sessionId), ("@user", userName), ("@day", dayKey), ("@hour", hourIndex),
            ("@minEnd", TimeUtil.ToIso(minEndUtc)));
        return rows.Count > 0 ? rows[0] : null;
    }

    public (double Seconds, string StartUtc, string EndUtc)? GetInterval(SqliteConnection cn, SqliteTransaction? tx, long id)
    {
        var rows = Db.Query(cn, tx,
            "SELECT DurationSeconds, StartUtc, EndUtc FROM ActivityIntervals WHERE Id=@id;",
            r => (Db.Dbl(r, 0), Db.Str(r, 1), Db.Str(r, 2)),
            ("@id", id));
        return rows.Count > 0 ? rows[0] : null;
    }

    public long InsertInterval(SqliteConnection cn, SqliteTransaction? tx, long appId, int sessionId, string userName,
        string dayKey, int hourIndex, DateTime startUtc, DateTime endUtc, double seconds, string? windowTitle)
    {
        Db.Exec(cn, tx,
            """
            INSERT INTO ActivityIntervals(ApplicationId, SessionId, UserName, DayKey, HourIndex, StartUtc, EndUtc, DurationSeconds, WindowTitle, UpdatedUtc)
            VALUES(@app, @sess, @user, @day, @hour, @start, @end, @sec, @title, @now);
            """,
            ("@app", appId), ("@sess", sessionId), ("@user", userName), ("@day", dayKey), ("@hour", hourIndex),
            ("@start", TimeUtil.ToIso(startUtc)), ("@end", TimeUtil.ToIso(endUtc)), ("@sec", seconds),
            ("@title", Truncate(windowTitle, 240)), ("@now", TimeUtil.ToIso(DateTime.UtcNow)));
        return Db.LastRowId(cn, tx);
    }

    public void UpdateInterval(SqliteConnection cn, SqliteTransaction? tx, long id, DateTime startUtc, DateTime endUtc,
        double seconds, string? windowTitle)
    {
        Db.Exec(cn, tx,
            """
            UPDATE ActivityIntervals
               SET StartUtc=@start, EndUtc=@end, DurationSeconds=@sec, WindowTitle=COALESCE(@title, WindowTitle), UpdatedUtc=@now
             WHERE Id=@id;
            """,
            ("@start", TimeUtil.ToIso(startUtc)), ("@end", TimeUtil.ToIso(endUtc)), ("@sec", seconds),
            ("@title", Truncate(windowTitle, 240)), ("@now", TimeUtil.ToIso(DateTime.UtcNow)), ("@id", id));
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : (value.Length <= max ? value : value[..max]);

    // ---------------------------------------------------------------- агрегаты

    /// <summary>Активность за сутки в разрезе приложение + сеанс + пользователь.</summary>
    public List<AppUsageRow> GetUsageRowsForDay(string dayKey)
    {
        using var cn = _db.Open();
        return Db.Query(cn,
            "SELECT ApplicationId, SessionId, UserName, SUM(DurationSeconds) FROM ActivityIntervals WHERE DayKey=@day GROUP BY ApplicationId, SessionId, UserName;",
            r => new AppUsageRow(Db.Lng(r, 0), Db.Int(r, 1), Db.Str(r, 2), Db.Dbl(r, 3)),
            ("@day", dayKey));
    }

    public Dictionary<long, double> GetAppSecondsForDay(string dayKey)
    {
        using var cn = _db.Open();
        var list = Db.Query(cn,
            "SELECT ApplicationId, SUM(DurationSeconds) FROM ActivityIntervals WHERE DayKey=@day GROUP BY ApplicationId;",
            r => new AppUsageItem(Db.Lng(r, 0), Db.Dbl(r, 1)),
            ("@day", dayKey));
        return list.ToDictionary(x => x.AppId, x => x.Seconds);
    }

    public List<HourAppUsage> GetHourAppUsage(string dayKey)
    {
        using var cn = _db.Open();
        return Db.Query(cn,
            "SELECT HourIndex, ApplicationId, SUM(DurationSeconds) FROM ActivityIntervals WHERE DayKey=@day GROUP BY HourIndex, ApplicationId;",
            r => new HourAppUsage(Db.Int(r, 0), Db.Lng(r, 1), Db.Dbl(r, 2)),
            ("@day", dayKey));
    }

    /// <summary>
    /// Интервалы, пересекающиеся с произвольным периодом [fromUtc, toUtc).
    /// Отрезки уже нарезаны по границам локальных часов, поэтому час и день берутся из самой записи.
    /// </summary>
    public List<RangeIntervalRow> GetIntervalsInRange(DateTime fromUtc, DateTime toUtc, int limit = 200_000)
    {
        using var cn = _db.Open();
        return Db.Query(cn,
            """
            SELECT i.Id, i.ApplicationId, a.ExeName, COALESCE(NULLIF(a.FriendlyName, ''), a.ExeName),
                   i.SessionId, i.UserName, i.StartUtc, i.EndUtc, i.DurationSeconds, i.DayKey, i.HourIndex
              FROM ActivityIntervals i
              JOIN Applications a ON a.Id = i.ApplicationId
             WHERE i.EndUtc > @from AND i.StartUtc < @to
             ORDER BY i.StartUtc
             LIMIT @limit;
            """,
            r => new RangeIntervalRow(
                Db.Lng(r, 0), Db.Lng(r, 1), Db.Str(r, 2), Db.Str(r, 3), Db.Int(r, 4), Db.Str(r, 5),
                TimeUtil.ParseIso(Db.Str(r, 6)), TimeUtil.ParseIso(Db.Str(r, 7)), Db.Dbl(r, 8), Db.Str(r, 9), Db.Int(r, 10)),
            ("@from", TimeUtil.ToIso(fromUtc)), ("@to", TimeUtil.ToIso(toUtc)), ("@limit", limit));
    }

    public List<IntervalRecord> GetIntervalsForApp(long appId, string dayKey)
    {
        using var cn = _db.Open();
        return Db.Query(cn, IntervalSelect + " WHERE i.DayKey=@day AND i.ApplicationId=@app ORDER BY i.StartUtc;",
            MapInterval, ("@day", dayKey), ("@app", appId));
    }

    public List<IntervalRecord> GetDayIntervals(string dayKey, int limit = 500)
    {
        using var cn = _db.Open();
        return Db.Query(cn, IntervalSelect + " WHERE i.DayKey=@day ORDER BY i.StartUtc LIMIT @limit;",
            MapInterval, ("@day", dayKey), ("@limit", limit));
    }

    public long CountIntervalsForDay(string dayKey) =>
        Db.Long(_db.Open(), "SELECT COUNT(1) FROM ActivityIntervals WHERE DayKey=@day;", ("@day", dayKey));

    public int PurgeBefore(string dayKeyExclusive)
    {
        using var cn = _db.Open();
        return Db.Exec(cn, "DELETE FROM ActivityIntervals WHERE DayKey < @day;", ("@day", dayKeyExclusive));
    }

    public int PurgeApplicationsNeverUsed(string olderThanUtc)
    {
        using var cn = _db.Open();
        return Db.Exec(cn,
            """
            DELETE FROM Applications
             WHERE LastSeenUtc < @older AND Id NOT IN (SELECT DISTINCT ApplicationId FROM ActivityIntervals);
            """,
            ("@older", olderThanUtc));
    }

    private const string IntervalSelect =
        """
        SELECT i.Id, i.ApplicationId, a.ExeName, COALESCE(NULLIF(a.FriendlyName, ''), a.ExeName),
               i.SessionId, i.UserName, i.StartUtc, i.EndUtc, i.DurationSeconds, i.DayKey, i.HourIndex
          FROM ActivityIntervals i
          JOIN Applications a ON a.Id = i.ApplicationId
        """;

    private static IntervalRecord MapInterval(SqliteDataReader r) => new(
        Db.Lng(r, 0), Db.Lng(r, 1), Db.Str(r, 2), Db.Str(r, 3), Db.Int(r, 4), Db.Str(r, 5),
        Db.Str(r, 6), Db.Str(r, 7), Db.Dbl(r, 8), Db.Str(r, 9), Db.Int(r, 10));

    // ---------------------------------------------------------------- группы

    public List<GroupRecord> ListGroups()
    {
        using var cn = _db.Open();
        return Db.Query(cn, "SELECT Id, Name, Description, Color, CreatedUtc FROM Groups ORDER BY Name COLLATE NOCASE;",
            r => new GroupRecord
            {
                Id = Db.Lng(r, 0),
                Name = Db.Str(r, 1),
                Description = Db.StrOrNull(r, 2),
                Color = Db.StrOrNull(r, 3),
                CreatedUtc = Db.Str(r, 4)
            });
    }

    public GroupRecord? GetGroup(long id)
    {
        using var cn = _db.Open();
        return Db.Query(cn, "SELECT Id, Name, Description, Color, CreatedUtc FROM Groups WHERE Id=@id;",
            r => new GroupRecord
            {
                Id = Db.Lng(r, 0),
                Name = Db.Str(r, 1),
                Description = Db.StrOrNull(r, 2),
                Color = Db.StrOrNull(r, 3),
                CreatedUtc = Db.Str(r, 4)
            },
            ("@id", id)).FirstOrDefault();
    }

    public long CreateGroup(string name, string? description, string? color)
    {
        using var cn = _db.Open();
        Db.Exec(cn, "INSERT INTO Groups(Name, Description, Color, CreatedUtc) VALUES(@name, @descr, @color, @now);",
            ("@name", name), ("@descr", description), ("@color", color), ("@now", TimeUtil.ToIso(DateTime.UtcNow)));
        return Db.LastRowId(cn, null);
    }

    public void UpdateGroup(long id, string name, string? description, string? color)
    {
        using var cn = _db.Open();
        Db.Exec(cn, "UPDATE Groups SET Name=@name, Description=@descr, Color=@color WHERE Id=@id;",
            ("@name", name), ("@descr", description), ("@color", color), ("@id", id));
    }

    public void DeleteGroup(long id)
    {
        using var cn = _db.Open();
        Db.Exec(cn, "DELETE FROM Groups WHERE Id=@id;", ("@id", id));
    }

    /// <summary>Массовое создание групп в одном запросе (используется сидером демо-данных).</summary>
    public void SetGroupMembers(long groupId, IEnumerable<long> appIds)
    {
        using var cn = _db.Open();
        using var tx = cn.BeginTransaction();
        Db.Exec(cn, tx, "DELETE FROM GroupMembers WHERE GroupId=@id;", ("@id", groupId));
        foreach (var appId in appIds.Distinct())
        {
            Db.Exec(cn, tx, "INSERT OR IGNORE INTO GroupMembers(GroupId, ApplicationId) VALUES(@g, @a);",
                ("@g", groupId), ("@a", appId));
        }
        tx.Commit();
    }

    public List<(long GroupId, long AppId)> ListGroupMembers()
    {
        using var cn = _db.Open();
        return Db.Query(cn, "SELECT GroupId, ApplicationId FROM GroupMembers;",
            r => (Db.Lng(r, 0), Db.Lng(r, 1)));
    }

    // ---------------------------------------------------------------- сеансы

    public void UpsertSession(int sessionId, string userName, string domain, string state, DateTime nowUtc)
    {
        using var cn = _db.Open();
        var now = TimeUtil.ToIso(nowUtc);
        Db.Exec(cn,
            """
            INSERT INTO Sessions(SessionId, UserName, Domain, StartedUtc, LastSeenUtc, State)
            VALUES(@id, @user, @domain, @now, @now, @state)
            ON CONFLICT(SessionId) DO UPDATE SET UserName=excluded.UserName, Domain=excluded.Domain,
                                                 LastSeenUtc=excluded.LastSeenUtc, State=excluded.State;
            """,
            ("@id", sessionId), ("@user", userName), ("@domain", domain), ("@state", state), ("@now", now));
    }

    public List<SessionRecord> ListSessions()
    {
        using var cn = _db.Open();
        return Db.Query(cn, "SELECT SessionId, UserName, Domain, StartedUtc, LastSeenUtc, State FROM Sessions ORDER BY SessionId;",
            r => new SessionRecord
            {
                SessionId = Db.Int(r, 0),
                UserName = Db.Str(r, 1),
                Domain = Db.Str(r, 2),
                StartedUtc = Db.Str(r, 3),
                LastSeenUtc = Db.Str(r, 4),
                State = Db.Str(r, 5)
            });
    }
}
