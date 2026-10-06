using System.Globalization;

namespace AppActive.Core;

/// <summary>
/// Работа со временем: в базе всё хранится в UTC (ISO-8601), а сутки и часы считаются
/// по локальному времени машины (DayKey = локальная дата, HourIndex = локальный час).
/// </summary>
public static class TimeUtil
{
    public const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fff'Z'";
    public const string DayKeyFormat = "yyyy-MM-dd";

    public static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static string ToIso(DateTime value) =>
        EnsureUtc(value).ToString(IsoFormat, CultureInfo.InvariantCulture);

    public static string ToIso(DateTime? value) => value.HasValue ? ToIso(value.Value) : "";

    public static DateTime ParseIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.UtcNow;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        return DateTime.UtcNow;
    }

    public static DateTime? TryParseIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        return null;
    }

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(utc), TimeZoneInfo.Local);

    public static DateTime ToUtc(DateTime localUnspecified) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified), TimeZoneInfo.Local);

    public static string DayKey(DateTime utc) =>
        ToLocal(utc).ToString(DayKeyFormat, CultureInfo.InvariantCulture);

    public static string TodayKey() => DayKey(DateTime.UtcNow);

    public static int HourIndex(DateTime utc) => ToLocal(utc).Hour;

    /// <summary>Начало локального часа, в который попадает момент (в UTC).</summary>
    public static DateTime HourStartUtc(DateTime utc)
    {
        var local = ToLocal(utc);
        var hourStart = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
        return ToUtc(hourStart);
    }

    public static DateTime NextHourStartUtc(DateTime utc) => HourStartUtc(utc).AddHours(1);

    /// <summary>Начало локальных суток по ключу даты (в UTC).</summary>
    public static DateTime DayStartUtc(string dayKey)
    {
        if (!DateTime.TryParseExact(dayKey, DayKeyFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            throw new ArgumentException($"Некорректный ключ даты: '{dayKey}'", nameof(dayKey));
        }
        return ToUtc(local);
    }

    public static DateTime DayEndUtc(string dayKey) => DayStartUtc(dayKey).AddDays(1);

    public static bool IsValidDayKey(string? dayKey) =>
        !string.IsNullOrWhiteSpace(dayKey) &&
        DateTime.TryParseExact(dayKey, DayKeyFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Ключ дня, отстоящий от указанного на deltaDays (локальный календарь).</summary>
    public static string ShiftDayKey(string dayKey, int deltaDays)
    {
        var local = DateTime.ParseExact(dayKey, DayKeyFormat, CultureInfo.InvariantCulture);
        return local.AddDays(deltaDays).ToString(DayKeyFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Человекочитаемая длительность: 2 ч 05 мин, 45 мин, 30 с.</summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var total = (int)Math.Round(seconds);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        if (h > 0) return $"{h} ч {m:00} мин";
        if (m > 0) return s > 0 ? $"{m} мин {s} с" : $"{m} мин";
        return $"{s} с";
    }
}
