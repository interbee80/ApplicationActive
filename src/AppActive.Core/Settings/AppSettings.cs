using System.Globalization;

namespace AppActive.Core.Settings;

/// <summary>Настраиваемые параметры учёта. Хранятся в таблице Settings парами ключ/значение.</summary>
public sealed class AppSettings
{
    /// <summary>Интервал опроса активного окна агентом, сек. Больше интервал — меньше нагрузка на ЦП.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>N: приложение активно, если в течение N секунд была активность клавиатуры или мыши.</summary>
    public int ActivityIdleSeconds { get; set; } = 60;

    /// <summary>Как часто служба сбрасывает накопленные секунды в SQLite, сек.</summary>
    public int FlushSeconds { get; set; } = 15;

    /// <summary>Сколько суток хранить историю активности.</summary>
    public int RetentionDays { get; set; } = 90;

    public bool MonitorEnabled { get; set; } = true;

    public bool CollectWindowTitles { get; set; } = true;

    /// <summary>Процессы, которые не учитываются (например logonui.exe).</summary>
    public List<string> IgnoreProcesses { get; set; } = new() { "logonui.exe", "lockapp.exe" };

    /// <summary>Не реже какого времени агент отчитывается, даже если активности нет, сек.</summary>
    public int AgentHeartbeatSeconds { get; set; } = 60;

    public string UpdatedUtc { get; set; } = "";

    public AppSettings Clone() => new()
    {
        PollIntervalSeconds = PollIntervalSeconds,
        ActivityIdleSeconds = ActivityIdleSeconds,
        FlushSeconds = FlushSeconds,
        RetentionDays = RetentionDays,
        MonitorEnabled = MonitorEnabled,
        CollectWindowTitles = CollectWindowTitles,
        IgnoreProcesses = new List<string>(IgnoreProcesses),
        AgentHeartbeatSeconds = AgentHeartbeatSeconds,
        UpdatedUtc = UpdatedUtc
    };

    public void Normalize()
    {
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, 1, 3600);
        ActivityIdleSeconds = Math.Clamp(ActivityIdleSeconds, 1, 86400);
        FlushSeconds = Math.Clamp(FlushSeconds, 1, 600);
        RetentionDays = Math.Clamp(RetentionDays, 1, 3650);
        AgentHeartbeatSeconds = Math.Clamp(AgentHeartbeatSeconds, 10, 3600);
        IgnoreProcesses = IgnoreProcesses
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    public bool IsIgnored(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        return IgnoreProcesses.Contains(exeName.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, string> ToDictionary() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PollIntervalSeconds"] = PollIntervalSeconds.ToString(CultureInfo.InvariantCulture),
        ["ActivityIdleSeconds"] = ActivityIdleSeconds.ToString(CultureInfo.InvariantCulture),
        ["FlushSeconds"] = FlushSeconds.ToString(CultureInfo.InvariantCulture),
        ["RetentionDays"] = RetentionDays.ToString(CultureInfo.InvariantCulture),
        ["MonitorEnabled"] = MonitorEnabled ? "true" : "false",
        ["CollectWindowTitles"] = CollectWindowTitles ? "true" : "false",
        ["IgnoreProcesses"] = string.Join('\n', IgnoreProcesses),
        ["AgentHeartbeatSeconds"] = AgentHeartbeatSeconds.ToString(CultureInfo.InvariantCulture)
    };

    public static AppSettings FromDictionary(IReadOnlyDictionary<string, string> map)
    {
        var s = new AppSettings();
        if (map.Count == 0) return s;

        s.PollIntervalSeconds = ReadInt(map, "PollIntervalSeconds", s.PollIntervalSeconds);
        s.ActivityIdleSeconds = ReadInt(map, "ActivityIdleSeconds", s.ActivityIdleSeconds);
        s.FlushSeconds = ReadInt(map, "FlushSeconds", s.FlushSeconds);
        s.RetentionDays = ReadInt(map, "RetentionDays", s.RetentionDays);
        s.MonitorEnabled = ReadBool(map, "MonitorEnabled", s.MonitorEnabled);
        s.CollectWindowTitles = ReadBool(map, "CollectWindowTitles", s.CollectWindowTitles);
        s.AgentHeartbeatSeconds = ReadInt(map, "AgentHeartbeatSeconds", s.AgentHeartbeatSeconds);
        if (map.TryGetValue("IgnoreProcesses", out var ignore) && !string.IsNullOrWhiteSpace(ignore))
        {
            s.IgnoreProcesses = ignore
                .Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        if (map.TryGetValue("UpdatedUtc", out var updated)) s.UpdatedUtc = updated;
        s.Normalize();
        return s;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> map, string key, int fallback) =>
        map.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, string> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var raw)) return fallback;
        return bool.TryParse(raw, out var value) ? value : (raw == "1" ? true : raw == "0" ? false : fallback);
    }
}
