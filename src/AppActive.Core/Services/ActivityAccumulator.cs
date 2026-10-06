using AppActive.Core.Data;
using AppActive.Core.Settings;
using Microsoft.Data.Sqlite;

namespace AppActive.Core.Services;

/// <summary>
/// Копилка активного времени. Агент присылает небольшие отрезки (тик опроса), служба складывает их
/// в отрезки по (приложение, сеанс, пользователь, локальный час) и периодически сбрасывает в SQLite,
/// продлевая одну строку вместо вставки тысяч новых. Разбиение по часам сделано заранее,
/// чтобы график «по часам» считался одним GROUP BY без вычислений в SQL.
/// </summary>
public sealed class ActivityAccumulator
{
    private readonly ActivityStore _store;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly object _gate = new();
    private readonly Dictionary<BucketKey, Bucket> _buckets = new();
    private readonly List<Bucket> _closed = new();

    public ActivityAccumulator(ActivityStore store, Func<AppSettings> settingsProvider)
    {
        _store = store;
        _settingsProvider = settingsProvider;
    }

    public long SamplesAccepted { get; private set; }

    public double SecondsAccepted { get; private set; }

    public long SamplesRejected { get; private set; }

    public DateTime? LastFlushUtc { get; private set; }

    public int LastFlushRows { get; private set; }

    public int PendingBuckets
    {
        get { lock (_gate) return _buckets.Count + _closed.Count; }
    }

    public double PendingSeconds
    {
        get { lock (_gate) return _buckets.Values.Sum(b => Math.Max(0, b.TotalSeconds - b.LastWritten)) + _closed.Sum(b => b.TotalSeconds); }
    }

    private readonly record struct BucketKey(long AppId, int SessionId, string UserName, string DayKey, int HourIndex);

    private sealed class Bucket
    {
        public long AppId { get; init; }
        public int SessionId { get; init; }
        public string UserName { get; init; } = "";
        public string DayKey { get; init; } = "";
        public int HourIndex { get; init; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public double TotalSeconds { get; set; }
        public string? WindowTitle { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public long RowId { get; set; }
        public double BaseSeconds { get; set; }
        public double LastWritten { get; set; } = -1;
    }

    private int GapSeconds()
    {
        var poll = Math.Max(1, _settingsProvider()?.PollIntervalSeconds ?? 5);
        return Math.Max(30, poll * 3 + 5);
    }

    /// <summary>
    /// Добавляет активное время приложения. Отрезок автоматически режется по границам локальных часов.
    /// Возвращает фактически принятое количество секунд.
    /// </summary>
    public double Add(long appId, int sessionId, string userName, DateTime startUtc, double seconds, string? windowTitle, DateTime? nowUtc = null)
    {
        if (seconds <= 0.01 || appId <= 0)
        {
            SamplesRejected++;
            return 0;
        }

        // Предохранитель от некорректных данных: один отрезок не может быть длиннее часа.
        if (seconds > 3600) seconds = 3600;

        var gap = GapSeconds();
        var cursor = TimeUtil.EnsureUtc(startUtc);
        var remaining = seconds;
        double added = 0;

        lock (_gate)
        {
            while (remaining > 0.01)
            {
                var hourStart = TimeUtil.HourStartUtc(cursor);
                var hourEnd = hourStart.AddHours(1);
                var chunk = Math.Min(remaining, (hourEnd - cursor).TotalSeconds);
                if (chunk <= 0.01) chunk = remaining;

                var key = new BucketKey(appId, sessionId, userName ?? "", TimeUtil.DayKey(cursor), TimeUtil.HourIndex(cursor));
                if (!_buckets.TryGetValue(key, out var bucket))
                {
                    bucket = NewBucket(key, cursor);
                    _buckets[key] = bucket;
                }
                else if (bucket.TotalSeconds > 0 && (cursor - bucket.LastActivityUtc).TotalSeconds > gap)
                {
                    // Пауза в активности — закрываем интервал, при возврате к приложению начнётся новый.
                    if (bucket.LastWritten < bucket.TotalSeconds) _closed.Add(bucket);
                    bucket = NewBucket(key, cursor);
                    _buckets[key] = bucket;
                }

                if (cursor < bucket.StartUtc) bucket.StartUtc = cursor;
                var end = cursor.AddSeconds(chunk);
                if (end > bucket.EndUtc) bucket.EndUtc = end;
                bucket.TotalSeconds += chunk;
                bucket.LastActivityUtc = end;
                if (!string.IsNullOrWhiteSpace(windowTitle)) bucket.WindowTitle = windowTitle;

                added += chunk;
                remaining -= chunk;
                cursor = end;
            }

            SamplesAccepted++;
            SecondsAccepted += added;
        }

        return added;
    }

    private static Bucket NewBucket(BucketKey key, DateTime cursor) => new()
    {
        AppId = key.AppId,
        SessionId = key.SessionId,
        UserName = key.UserName,
        DayKey = key.DayKey,
        HourIndex = key.HourIndex,
        StartUtc = cursor,
        EndUtc = cursor,
        LastActivityUtc = cursor
    };

    private readonly record struct Snapshot(
        long AppId,
        int SessionId,
        string UserName,
        string DayKey,
        int HourIndex,
        DateTime StartUtc,
        DateTime EndUtc,
        double TotalSeconds,
        string? WindowTitle,
        long RowId,
        double BaseSeconds);

    private static Snapshot Take(Bucket b) => new(
        b.AppId, b.SessionId, b.UserName, b.DayKey, b.HourIndex, b.StartUtc, b.EndUtc,
        b.TotalSeconds, b.WindowTitle, b.RowId, b.BaseSeconds);

    /// <summary>Сбрасывает накопленные секунды в SQLite. Возвращает число обновлённых строк.</summary>
    public int Flush()
    {
        var items = new List<(Bucket Bucket, Snapshot Snap, bool Closed)>();
        lock (_gate)
        {
            foreach (var bucket in _buckets.Values)
            {
                if (bucket.TotalSeconds <= 0) continue;
                if (bucket.LastWritten >= 0 && bucket.BaseSeconds + bucket.TotalSeconds - bucket.LastWritten < 0.05) continue;
                items.Add((bucket, Take(bucket), false));
            }
            foreach (var bucket in _closed)
            {
                items.Add((bucket, Take(bucket), true));
            }
        }

        if (items.Count == 0)
        {
            LastFlushUtc = DateTime.UtcNow;
            LastFlushRows = 0;
            return 0;
        }

        var minEnd = DateTime.UtcNow.AddSeconds(-GapSeconds());
        var written = 0;
        var persisted = new List<(Bucket Bucket, double PersistedValue, long RowId, double BaseSeconds, bool Closed)>();

        try
        {
            using var cn = _store.Open();
            using var tx = cn.BeginTransaction();
            foreach (var (bucket, snap, closed) in items)
            {
                try
                {
                    var rowId = snap.RowId;
                    var baseSeconds = snap.BaseSeconds;

                    if (rowId == 0)
                    {
                        var found = _store.FindOpenIntervalRow(cn, tx, snap.AppId, snap.SessionId, snap.UserName, snap.DayKey, snap.HourIndex, minEnd);
                        if (found.HasValue)
                        {
                            rowId = found.Value;
                            baseSeconds = _store.GetInterval(cn, tx, rowId)?.Seconds ?? 0;
                        }
                        else
                        {
                            rowId = _store.InsertInterval(cn, tx, snap.AppId, snap.SessionId, snap.UserName, snap.DayKey,
                                snap.HourIndex, snap.StartUtc, snap.EndUtc, 0, snap.WindowTitle);
                            baseSeconds = 0;
                        }
                    }

                    var start = snap.StartUtc;
                    var end = snap.EndUtc;
                    var existing = _store.GetInterval(cn, tx, rowId);
                    if (existing.HasValue)
                    {
                        var existingStart = TimeUtil.TryParseIso(existing.Value.StartUtc);
                        if (existingStart.HasValue && existingStart.Value < start) start = existingStart.Value;
                        var existingEnd = TimeUtil.TryParseIso(existing.Value.EndUtc);
                        if (existingEnd.HasValue && existingEnd.Value > end) end = existingEnd.Value;
                    }

                    var total = baseSeconds + snap.TotalSeconds;
                    _store.UpdateInterval(cn, tx, rowId, start, end, total, snap.WindowTitle);
                    persisted.Add((bucket, total, rowId, baseSeconds, closed));
                    written++;
                }
                catch (SqliteException)
                {
                    // Пропускаем этот отрезок — попробуем на следующем сбросе.
                }
            }

            tx.Commit();
        }
        catch (SqliteException)
        {
            LastFlushUtc = DateTime.UtcNow;
            LastFlushRows = 0;
            return 0;
        }

        lock (_gate)
        {
            foreach (var (bucket, value, rowId, baseSeconds, closed) in persisted)
            {
                bucket.LastWritten = value;
                bucket.RowId = rowId;
                bucket.BaseSeconds = baseSeconds;
                if (closed) _closed.Remove(bucket);
            }
        }

        LastFlushUtc = DateTime.UtcNow;
        LastFlushRows = written;
        return written;
    }

    /// <summary>Полный цикл завершения: записать всё и обнулить состояние (используется при остановке службы).</summary>
    public void FlushAndReset()
    {
        Flush();
        lock (_gate)
        {
            _buckets.Clear();
            _closed.Clear();
        }
    }
}
