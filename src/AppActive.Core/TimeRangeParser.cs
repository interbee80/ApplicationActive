using System.Globalization;

namespace AppActive.Core;

/// <summary>Разобранный период наблюдения.</summary>
public sealed class TimeRange
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public string FromText { get; init; } = "";
    public string ToText { get; init; } = "";

    public double TotalSeconds => (ToUtc - FromUtc).TotalSeconds;

    public string Label => $"{TimeUtil.ToLocal(FromUtc):dd.MM.yyyy HH:mm} — {TimeUtil.ToLocal(ToUtc):dd.MM.yyyy HH:mm}";
}

/// <summary>
/// Разбор периода из параметров запроса. Поддерживаются:
///   • пустые значения — сутки выбранной даты;
///   • дата 2026-10-06 (в начале периода — начало суток, в конце — конец суток);
///   • дата и время 2026-10-06T10:30, 2026-10-06 10:30 (локальное время сервера);
///   • абсолютное время с зоной 2026-10-06T10:30:00Z;
///   • относительные выражения как в Grafana: now, now-1h, now+30m, now-7d, today, yesterday;
///   • единицы: s, m (минуты), h, d, w, M (месяцы), y.
/// </summary>
public static class TimeRangeParser
{
    public const int MaxDays = 366;

    public static bool TryParse(string? from, string? to, string? date, out TimeRange range, out string error)
    {
        range = new TimeRange { FromUtc = DateTime.UtcNow, ToUtc = DateTime.UtcNow };
        error = "";

        var now = DateTime.UtcNow;
        var hasRange = !string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to);

        if (!hasRange)
        {
            var dayKey = TimeUtil.IsValidDayKey(date) ? date! : TimeUtil.TodayKey();
            range = new TimeRange
            {
                FromUtc = TimeUtil.DayStartUtc(dayKey),
                ToUtc = TimeUtil.DayEndUtc(dayKey),
                FromText = dayKey,
                ToText = dayKey
            };
            return true;
        }

        if (!TryParsePoint(from, now, isEnd: false, out var fromUtc, out var fromError))
        {
            error = $"Не удалось разобрать начало периода '{from}': {fromError}";
            return false;
        }
        if (!TryParsePoint(to, now, isEnd: true, out var toUtc, out var toError))
        {
            error = $"Не удалось разобрать конец периода '{to}': {toError}";
            return false;
        }

        if (toUtc <= fromUtc)
        {
            error = "Конец периода должен быть позже начала";
            return false;
        }

        if ((toUtc - fromUtc).TotalDays > MaxDays)
        {
            error = $"Период больше {MaxDays} суток — сузьте диапазон";
            return false;
        }

        range = new TimeRange
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            FromText = string.IsNullOrWhiteSpace(from) ? TimeUtil.ToIso(fromUtc) : from!.Trim(),
            ToText = string.IsNullOrWhiteSpace(to) ? TimeUtil.ToIso(toUtc) : to!.Trim()
        };
        return true;
    }

    public static bool TryParsePoint(string? text, DateTime nowUtc, bool isEnd, out DateTime utc, out string error)
    {
        error = "";
        var value = (text ?? "").Trim();

        if (value.Length == 0)
        {
            // Пустая граница: начало — начало сегодняшних суток, конец — сейчас.
            utc = isEnd ? nowUtc : TimeUtil.DayStartUtc(TimeUtil.DayKey(nowUtc));
            return true;
        }

        switch (value.ToLowerInvariant())
        {
            case "now":
                utc = nowUtc;
                return true;
        }

        if (value.StartsWith("now", StringComparison.OrdinalIgnoreCase))
        {
            var tail = value[3..];
            if (tail.Length == 0 || tail.StartsWith('(') && tail.EndsWith(')'))
            {
                utc = nowUtc;
                return true;
            }

            var sign = 1;
            if (tail[0] == '+') tail = tail[1..];
            else if (tail[0] == '-')
            {
                sign = -1;
                tail = tail[1..];
            }

            if (!TryParseDuration(tail, out var span, out error))
            {
                error = "ожидается формат now-1h, now+30m, now-7d";
                utc = nowUtc;
                return false;
            }

            utc = nowUtc.AddTicks(sign * span.Ticks);
            return true;
        }

        if (value.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            var start = TimeUtil.DayStartUtc(TimeUtil.DayKey(nowUtc));
            utc = isEnd ? start.AddDays(1).AddTicks(-1) : start;
            return true;
        }

        if (value.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
        {
            var start = TimeUtil.DayStartUtc(TimeUtil.DayKey(nowUtc)).AddDays(-1);
            utc = isEnd ? start.AddDays(1).AddTicks(-1) : start;
            return true;
        }

        // Абсолютное время со зоной (Z или смещение) либо локальное.
        var hasZone = value.EndsWith('Z') || value.EndsWith('z') ||
                      System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d{2}:?\d{2}$");

        var formats = new[]
        {
            "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "dd.MM.yyyy HH:mm", "dd.MM.yyyy"
        };

        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            utc = TimeUtil.ToUtc(local);
            if (isEnd)
            {
                if (!value.Contains(':'))
                {
                    // Указана только дата — конец периода означает конец этих суток.
                    utc = utc.AddDays(1).AddTicks(-1);
                }
                else if (value.Length <= 16)
                {
                    // Указана минута — включаем её целиком.
                    utc = utc.AddMinutes(1).AddTicks(-1);
                }
            }
            return true;
        }

        if (hasZone && DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var absolute))
        {
            utc = DateTime.SpecifyKind(absolute, DateTimeKind.Utc);
            return true;
        }

        error = "поддерживаются 2026-10-06, 2026-10-06T10:30, now, now-1h, now+30m, today, yesterday";
        utc = nowUtc;
        return false;
    }

    /// <summary>Длительность в формате Grafana: 30s, 15m, 1h, 7d, 2w, 1M, 1y (можно комбинировать: 1h30m).</summary>
    public static bool TryParseDuration(string text, out TimeSpan span, out string error)
    {
        span = TimeSpan.Zero;
        error = "";
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            error = "пустая длительность";
            return false;
        }

        var total = TimeSpan.Zero;
        var index = 0;
        var matchedAny = false;

        while (index < value.Length)
        {
            var start = index;
            while (index < value.Length && char.IsDigit(value[index])) index++;
            if (start == index)
            {
                error = $"не число в '{value[start..]}'";
                return false;
            }

            var amount = int.Parse(value[start..index], CultureInfo.InvariantCulture);
            if (index >= value.Length)
            {
                error = "не указана единица измерения (s, m, h, d, w, M, y)";
                return false;
            }

            var unit = value[index++];
            switch (unit)
            {
                case 's': case 'S': total = total.Add(TimeSpan.FromSeconds(amount)); break;
                case 'm': total = total.Add(TimeSpan.FromMinutes(amount)); break;
                case 'h': case 'H': total = total.Add(TimeSpan.FromHours(amount)); break;
                case 'd': case 'D': total = total.Add(TimeSpan.FromDays(amount)); break;
                case 'w': case 'W': total = total.Add(TimeSpan.FromDays(7 * amount)); break;
                case 'M': total = total.Add(TimeSpan.FromDays(30 * amount)); break;
                case 'y': case 'Y': total = total.Add(TimeSpan.FromDays(365 * amount)); break;
                default:
                    error = $"неизвестная единица '{unit}' (s, m, h, d, w, M, y)";
                    return false;
            }
            matchedAny = true;
        }

        if (!matchedAny || total <= TimeSpan.Zero)
        {
            error = "длительность должна быть больше нуля";
            return false;
        }

        span = total;
        return true;
    }
}
