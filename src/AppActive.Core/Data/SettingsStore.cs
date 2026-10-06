using AppActive.Core.Options;
using AppActive.Core.Settings;

namespace AppActive.Core.Data;

/// <summary>Хранилище пар ключ/значение для настроек.</summary>
public sealed class SettingsStore
{
    private readonly SqliteDatabase _db;

    public SettingsStore(SqliteDatabase database) => _db = database;

    public Dictionary<string, string> LoadAll()
    {
        using var cn = _db.Open();
        var rows = Db.Query(cn, "SELECT Key, Value FROM Settings;", r => (Db.Str(r, 0), Db.Str(r, 1)));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rows) map[key] = value;
        return map;
    }

    public void SaveMany(IEnumerable<KeyValuePair<string, string>> values, string updatedUtc)
    {
        using var cn = _db.Open();
        using var tx = cn.BeginTransaction();
        foreach (var (key, value) in values)
        {
            Db.Exec(cn, tx,
                """
                INSERT INTO Settings(Key, Value, UpdatedUtc) VALUES(@key, @value, @now)
                  ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value, UpdatedUtc=excluded.UpdatedUtc;
                """,
                ("@key", key), ("@value", value ?? ""), ("@now", updatedUtc));
        }
        tx.Commit();
    }

    public void Save(string key, string value)
    {
        SaveMany(new[] { new KeyValuePair<string, string>(key, value) }, TimeUtil.ToIso(DateTime.UtcNow));
    }
}

/// <summary>Настройки в памяти с уведомлением об изменениях (агенты получают их в ответе на отчёт).</summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;
    private readonly object _gate = new();
    private AppSettings _current;

    /// <param name="store">Хранилище настроек в базе.</param>
    /// <param name="initial">
    /// Начальные значения из appsettings.json (AppActive:Monitoring:Initial). Добавляются в базу,
    /// только если соответствующих ключей в ней ещё нет, — значения, изменённые из Web-панели, всегда приоритетнее.
    /// </param>
    public SettingsService(SettingsStore store, MonitoringInitialOptions? initial = null)
    {
        _store = store;
        var stored = _store.LoadAll();

        var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (initial is not null)
        {
            foreach (var pair in FromInitial(initial))
            {
                if (!stored.ContainsKey(pair.Key)) missing[pair.Key] = pair.Value;
            }
        }

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in missing) merged[pair.Key] = pair.Value;
        foreach (var pair in stored) merged[pair.Key] = pair.Value;

        var loaded = AppSettings.FromDictionary(merged);
        loaded.Normalize();
        if (string.IsNullOrWhiteSpace(loaded.UpdatedUtc)) loaded.UpdatedUtc = TimeUtil.ToIso(DateTime.UtcNow);
        _current = loaded;

        if (missing.Count > 0)
        {
            try
            {
                _store.SaveMany(missing, loaded.UpdatedUtc);
                AppLog.Info($"Параметры учёта проинициализированы из appsettings.json (ключей: {missing.Count}): " +
                            $"интервал опроса {loaded.PollIntervalSeconds} с, N={loaded.ActivityIdleSeconds} с, " +
                            $"сброс {loaded.FlushSeconds} с, хранение {loaded.RetentionDays} сут");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось сохранить начальные параметры учёта: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, string> FromInitial(MonitoringInitialOptions initial)
    {
        var settings = new AppSettings
        {
            PollIntervalSeconds = initial.PollIntervalSeconds,
            ActivityIdleSeconds = initial.ActivityIdleSeconds,
            FlushSeconds = initial.FlushSeconds,
            RetentionDays = initial.RetentionDays,
            AgentHeartbeatSeconds = initial.AgentHeartbeatSeconds,
            MonitorEnabled = initial.MonitorEnabled,
            CollectWindowTitles = initial.CollectWindowTitles,
            IgnoreProcesses = initial.IgnoreProcesses.ToList()
        };
        settings.Normalize();
        return settings.ToDictionary();
    }

    public event Action<AppSettings>? Changed;

    /// <summary>Текущие настройки. Объект заменяется целиком, поэтому читать его безопасно.</summary>
    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public AppSettings Snapshot()
    {
        lock (_gate) return _current.Clone();
    }

    public AppSettings Update(Action<AppSettings> mutate)
    {
        AppSettings updated;
        lock (_gate)
        {
            var copy = _current.Clone();
            mutate(copy);
            copy.Normalize();
            copy.UpdatedUtc = TimeUtil.ToIso(DateTime.UtcNow);
            _store.SaveMany(copy.ToDictionary().Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
                .Append(new KeyValuePair<string, string>("UpdatedUtc", copy.UpdatedUtc)), copy.UpdatedUtc);
            _current = copy;
            updated = copy;
        }

        try
        {
            Changed?.Invoke(updated);
        }
        catch
        {
            // Подписчики не должны ломать сохранение настроек.
        }

        return updated;
    }

    public void Reload()
    {
        AppSettings loaded;
        lock (_gate)
        {
            loaded = AppSettings.FromDictionary(_store.LoadAll());
            loaded.Normalize();
            _current = loaded;
        }
        Changed?.Invoke(loaded);
    }
}
