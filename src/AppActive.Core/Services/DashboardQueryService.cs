using AppActive.Core.Data;
using AppActive.Core.Models;
using AppActive.Core.Settings;

namespace AppActive.Core.Services;

/// <summary>Поставщик статусов агентов (реализуется службой, ядру знать про процессы не нужно).</summary>
public interface IAgentStatusProvider
{
    IReadOnlyList<AgentStatusDto> GetAgentStatuses(bool includeInactive);
}

/// <summary>Сборка ответов Web-панели: dashboard, список приложений, группы, правила.</summary>
public sealed class DashboardQueryService
{
    private readonly ActivityStore _activity;
    private readonly RulesStore _rules;
    private readonly CriticalEvaluator _critical;
    private readonly SettingsService _settings;
    private readonly IAgentStatusProvider? _agents;

    public DashboardQueryService(
        ActivityStore activity,
        RulesStore rules,
        CriticalEvaluator critical,
        SettingsService settings,
        IAgentStatusProvider? agents = null)
    {
        _activity = activity;
        _rules = rules;
        _critical = critical;
        _settings = settings;
        _agents = agents;
    }

    private sealed class DayData
    {
        public required Dictionary<long, ApplicationRecord> Apps { get; init; }
        public required List<AppUsageRow> Usage { get; init; }
        public required Dictionary<long, double> AppSeconds { get; init; }
        public required Dictionary<long, GroupRecord> Groups { get; init; }
        public required Dictionary<long, List<long>> GroupMembers { get; init; }
        public required List<CriticalStatus> Statuses { get; init; }
        public double TrackedSeconds { get; init; }
    }

    private DayData Load(string dayKey)
    {
        var apps = _activity.ListApplications().ToDictionary(a => a.Id);
        var usage = _activity.GetUsageRowsForDay(dayKey);
        var groups = _activity.ListGroups().ToDictionary(g => g.Id);
        var groupMembers = _activity.ListGroupMembers()
            .GroupBy(x => x.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.AppId).ToList());

        var visible = usage.Where(u => !apps.TryGetValue(u.AppId, out var a) || !a.IsIgnored).ToList();
        var appSeconds = visible.GroupBy(u => u.AppId).ToDictionary(g => g.Key, g => g.Sum(x => x.Seconds));

        var statuses = _critical.Evaluate(dayKey, appSeconds, apps, groups, groupMembers);

        return new DayData
        {
            Apps = apps,
            Usage = usage,
            AppSeconds = appSeconds,
            Groups = groups,
            GroupMembers = groupMembers,
            Statuses = statuses,
            TrackedSeconds = appSeconds.Values.Sum()
        };
    }

    // ---------------------------------------------------------------- dashboard

    /// <summary>
    /// Ответ Web-панели. Без периода строится за сутки <paramref name="dayKey"/>,
    /// с периодом (в том числе относительным: now-1d … now) — за произвольный диапазон.
    /// </summary>
    public DashboardPayload BuildDashboard(string dayKey, TimeRange? range = null)
    {
        var payload = BuildDashboardForDay(dayKey);

        if (range is null)
        {
            var from = TimeUtil.DayStartUtc(dayKey);
            var to = TimeUtil.DayEndUtc(dayKey);
            payload.Range = new DashboardRangeDto
            {
                FromUtc = TimeUtil.ToIso(from),
                ToUtc = TimeUtil.ToIso(to),
                FromLocal = TimeUtil.ToLocal(from).ToString("yyyy-MM-dd HH:mm"),
                ToLocal = TimeUtil.ToLocal(to).ToString("yyyy-MM-dd HH:mm"),
                FromText = dayKey,
                ToText = dayKey,
                Label = $"{TimeUtil.ToLocal(from):dd.MM.yyyy} (сутки)",
                BucketKind = "hour",
                BucketCount = payload.Hours.Count,
                TotalSeconds = payload.Totals.TrackedSeconds
            };

            // Часовые столбцы приводим к общему виду, чтобы панель работала одинаково.
            payload.Buckets = payload.Hours
                .Select(h => new DashboardBucketDto
                {
                    Key = dayKey + "T" + h.Hour.ToString("00"),
                    Label = h.Hour.ToString("00") + ":00",
                    StartUtc = TimeUtil.ToIso(TimeUtil.DayStartUtc(dayKey).AddHours(h.Hour)),
                    EndUtc = TimeUtil.ToIso(TimeUtil.DayStartUtc(dayKey).AddHours(h.Hour + 1)),
                    IsCurrent = h.IsCurrent,
                    TotalSeconds = h.TotalSeconds,
                    Apps = h.Apps
                })
                .ToList();
        }
        else
        {
            ApplyRange(payload, range);
        }

        ApplyAppColors(payload);
        return payload;
    }

    /// <summary>Пересчитывает график, топ приложений, пользователей и ленту дня по произвольному периоду.</summary>
    private void ApplyRange(DashboardPayload payload, TimeRange range)
    {
        var from = range.FromUtc;
        var to = range.ToUtc;
        var hourly = range.TotalSeconds <= 48 * 3600;
        var now = DateTime.UtcNow;

        var data = Load(TimeUtil.DayKey(to.AddTicks(-1)));
        var apps = data.Apps;

        var rows = _activity.GetIntervalsInRange(from, to);

        // Каркас столбцов: часы для короткого периода, сутки для длинного.
        var buckets = new List<DashboardBucketDto>();
        var bucketMap = new Dictionary<string, DashboardBucketDto>(StringComparer.Ordinal);
        if (hourly)
        {
            var cursor = TimeUtil.HourStartUtc(from);
            while (cursor < to)
            {
                var hourEnd = cursor.AddHours(1);
                var key = TimeUtil.DayKey(cursor) + "T" + TimeUtil.HourIndex(cursor).ToString("00");
                if (!bucketMap.ContainsKey(key))
                {
                    var bucket = new DashboardBucketDto
                    {
                        Key = key,
                        Label = TimeUtil.HourIndex(cursor).ToString("00") + ":00",
                        StartUtc = TimeUtil.ToIso(cursor),
                        EndUtc = TimeUtil.ToIso(hourEnd),
                        IsCurrent = now >= cursor && now < hourEnd,
                        IsPartial = cursor < from || hourEnd > to
                    };
                    bucketMap[key] = bucket;
                    buckets.Add(bucket);
                }
                cursor = hourEnd;
            }
        }
        else
        {
            var cursor = TimeUtil.DayStartUtc(TimeUtil.DayKey(from));
            while (cursor < to)
            {
                var dayEnd = cursor.AddDays(1);
                var key = TimeUtil.DayKey(cursor);
                if (!bucketMap.ContainsKey(key))
                {
                    var bucket = new DashboardBucketDto
                    {
                        Key = key,
                        Label = TimeUtil.ToLocal(cursor).ToString("dd.MM"),
                        StartUtc = TimeUtil.ToIso(cursor),
                        EndUtc = TimeUtil.ToIso(dayEnd),
                        IsCurrent = now >= cursor && now < dayEnd,
                        IsPartial = cursor < from || dayEnd > to
                    };
                    bucketMap[key] = bucket;
                    buckets.Add(bucket);
                }
                cursor = dayEnd;
            }
        }

        var appSeconds = new Dictionary<long, double>();
        var appSessions = new Dictionary<long, HashSet<int>>();
        var appUsers = new Dictionary<long, HashSet<string>>();
        var users = new Dictionary<string, (double Seconds, HashSet<int> Sessions)>(StringComparer.OrdinalIgnoreCase);
        var timeline = new List<DashboardTimelineItemDto>();
        var bucketApps = new Dictionary<string, Dictionary<long, double>>(StringComparer.Ordinal);
        double tracked = 0;
        var sessionIds = new HashSet<int>();

        foreach (var row in rows)
        {
            if (apps.TryGetValue(row.AppId, out var app) && app.IsIgnored) continue;

            var seconds = row.SecondsInRange(from, to);
            if (seconds <= 0.01) continue;

            tracked += seconds;
            appSeconds[row.AppId] = appSeconds.GetValueOrDefault(row.AppId) + seconds;
            if (!appSessions.TryGetValue(row.AppId, out var sessionsOfApp))
            {
                sessionsOfApp = new HashSet<int>();
                appSessions[row.AppId] = sessionsOfApp;
            }
            sessionsOfApp.Add(row.SessionId);
            if (!appUsers.TryGetValue(row.AppId, out var usersOfApp))
            {
                usersOfApp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                appUsers[row.AppId] = usersOfApp;
            }
            usersOfApp.Add(row.UserName);
            sessionIds.Add(row.SessionId);

            if (!users.TryGetValue(row.UserName, out var user))
            {
                user = (0, new HashSet<int>());
            }
            user.Seconds += seconds;
            user.Sessions.Add(row.SessionId);
            users[row.UserName] = user;

            var key = hourly ? row.DayKey + "T" + row.HourIndex.ToString("00") : row.DayKey;
            if (bucketMap.TryGetValue(key, out var bucket))
            {
                if (!bucketApps.TryGetValue(key, out var perApp))
                {
                    perApp = new Dictionary<long, double>();
                    bucketApps[key] = perApp;
                }
                perApp[row.AppId] = perApp.GetValueOrDefault(row.AppId) + seconds;
                bucket.TotalSeconds = Math.Round(bucket.TotalSeconds + seconds, 1);
            }

            var start = row.StartUtc > from ? row.StartUtc : from;
            var end = row.EndUtc < to ? row.EndUtc : to;
            timeline.Add(new DashboardTimelineItemDto
            {
                Id = row.Id,
                AppId = row.AppId,
                ExeName = row.ExeName,
                DisplayName = row.DisplayName,
                UserName = row.UserName,
                SessionId = row.SessionId,
                StartUtc = TimeUtil.ToIso(start),
                EndUtc = TimeUtil.ToIso(end),
                Seconds = Math.Round(seconds, 1),
                HourIndex = row.HourIndex
            });
        }

        foreach (var bucket in buckets)
        {
            if (!bucketApps.TryGetValue(bucket.Key, out var perApp)) continue;
            foreach (var pair in perApp.OrderByDescending(p => p.Value))
            {
                apps.TryGetValue(pair.Key, out var app);
                bucket.Apps.Add(new DashboardHourAppDto
                {
                    AppId = pair.Key,
                    ExeName = app?.ExeName ?? $"#{pair.Key}",
                    DisplayName = app?.DisplayName ?? $"#{pair.Key}",
                    Seconds = Math.Round(pair.Value, 1)
                });
            }
        }

        payload.Range = new DashboardRangeDto
        {
            FromUtc = TimeUtil.ToIso(from),
            ToUtc = TimeUtil.ToIso(to),
            FromLocal = TimeUtil.ToLocal(from).ToString("yyyy-MM-dd HH:mm"),
            ToLocal = TimeUtil.ToLocal(to).ToString("yyyy-MM-dd HH:mm"),
            FromText = range.FromText,
            ToText = range.ToText,
            Label = $"{TimeUtil.ToLocal(from):dd.MM.yyyy HH:mm} — {TimeUtil.ToLocal(to):dd.MM.yyyy HH:mm}",
            BucketKind = hourly ? "hour" : "day",
            BucketCount = buckets.Count,
            TotalSeconds = Math.Round(tracked, 1)
        };
        payload.Buckets = buckets;

        // Совместимость с прежним видом ответа: часы внутри одних суток.
        payload.Hours = hourly && TimeUtil.DayKey(from) == TimeUtil.DayKey(to.AddTicks(-1))
            ? buckets.Select(b => new DashboardHourDto
            {
                Hour = int.TryParse(b.Key[^2..], out var hour) ? hour : 0,
                TotalSeconds = b.TotalSeconds,
                IsCurrent = b.IsCurrent,
                Apps = b.Apps
            }).ToList()
            : new List<DashboardHourDto>();

        payload.Totals.TrackedSeconds = Math.Round(tracked, 1);
        payload.Totals.AppCount = appSeconds.Count;
        payload.Totals.SessionCount = sessionIds.Count;
        payload.Totals.UserCount = users.Count;
        payload.Totals.MonitoredAppCount = apps.Count;

        payload.TopApps = appSeconds
            .OrderByDescending(pair => pair.Value)
            .Select(pair => BuildRangeApp(pair.Key, pair.Value, tracked, apps, data,
                appSessions.GetValueOrDefault(pair.Key), appUsers.GetValueOrDefault(pair.Key)))
            .ToList();

        payload.PerUser = users
            .Select(pair => new DashboardUserDto
            {
                UserName = pair.Key,
                Seconds = Math.Round(pair.Value.Seconds, 1),
                Percent = Percent(pair.Value.Seconds, tracked),
                SessionIds = pair.Value.Sessions.OrderBy(x => x).ToList()
            })
            .OrderByDescending(u => u.Seconds)
            .ToList();

        payload.Timeline = timeline
            .Where(i => i.Seconds > 0)
            .OrderByDescending(i => i.Seconds)
            .Take(600)
            .OrderBy(i => i.StartUtc, StringComparer.Ordinal)
            .ToList();
    }

    private DashboardAppDto BuildRangeApp(long appId, double seconds, double tracked,
        IReadOnlyDictionary<long, ApplicationRecord> apps, DayData data,
        HashSet<int>? sessions, HashSet<string>? users)
    {
        apps.TryGetValue(appId, out var app);
        var groups = data.GroupMembers.Where(g => g.Value.Contains(appId)).Select(g => g.Key).ToList();
        return new DashboardAppDto
        {
            AppId = appId,
            ExeName = app?.ExeName ?? $"#{appId}",
            DisplayName = app?.DisplayName ?? $"#{appId}",
            FriendlyName = app?.FriendlyName,
            ExePath = app?.ExePath,
            Seconds = Math.Round(seconds, 1),
            Percent = Percent(seconds, tracked),
            SessionCount = sessions?.Count ?? 0,
            UserNames = users?.OrderBy(x => x).ToList() ?? new List<string>(),
            GroupIds = groups,
            GroupNames = groups.Select(g => data.Groups.TryGetValue(g, out var gr) ? gr.Name : $"#{g}").ToList(),
            Critical = PickLimit(data, appId)
        };
    }

    /// <summary>Цвета приложений: верхние по времени получают самые различимые цвета палитры.</summary>
    private static void ApplyAppColors(DashboardPayload payload)
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var app in payload.TopApps)
        {
            colors[app.AppId.ToString()] = AppPalette.ForIndex(index++);
        }
        foreach (var hour in payload.Buckets)
        {
            foreach (var app in hour.Apps)
            {
                var key = app.AppId.ToString();
                if (!colors.ContainsKey(key)) colors[key] = AppPalette.ForIndex(index++);
            }
        }
        payload.AppColors = colors;
    }

    private DashboardPayload BuildDashboardForDay(string dayKey)
    {
        var settings = _settings.Current;
        var data = Load(dayKey);
        var payload = new DashboardPayload
        {
            Date = dayKey,
            GeneratedUtc = TimeUtil.ToIso(DateTime.UtcNow),
            Settings = new DashboardSettingsDto
            {
                PollIntervalSeconds = settings.PollIntervalSeconds,
                ActivityIdleSeconds = settings.ActivityIdleSeconds,
                MonitorEnabled = settings.MonitorEnabled,
                RetentionDays = settings.RetentionDays
            }
        };

        // Сводные показатели
        payload.Totals.TrackedSeconds = Math.Round(data.TrackedSeconds, 1);
        payload.Totals.AppCount = data.AppSeconds.Count;
        payload.Totals.SessionCount = data.Usage.Where(u => u.Seconds > 0).Select(u => u.SessionId).Distinct().Count();
        payload.Totals.UserCount = data.Usage.Where(u => u.Seconds > 0).Select(u => u.UserName).Distinct().Count();
        payload.Totals.CriticalRuleCount = data.Statuses.Count(s => s.Enabled);
        payload.Totals.CriticalExceededCount = data.Statuses.Count(s => s.Exceeded);
        payload.Totals.MonitoredAppCount = data.Apps.Count;

        // Часы
        var hourRows = _activity.GetHourAppUsage(dayKey);
        var currentHour = TimeUtil.DayKey(DateTime.UtcNow) == dayKey ? TimeUtil.HourIndex(DateTime.UtcNow) : -1;
        var hours = new List<DashboardHourDto>();
        for (var hour = 0; hour < 24; hour++)
        {
            var rows = hourRows.Where(r => r.HourIndex == hour).ToList();
            var dto = new DashboardHourDto
            {
                Hour = hour,
                TotalSeconds = Math.Round(rows.Where(r => data.AppSeconds.ContainsKey(r.AppId)).Sum(r => r.Seconds), 1),
                IsCurrent = hour == currentHour
            };
            foreach (var row in rows.Where(r => data.AppSeconds.ContainsKey(r.AppId)).OrderByDescending(r => r.Seconds))
            {
                data.Apps.TryGetValue(row.AppId, out var app);
                dto.Apps.Add(new DashboardHourAppDto
                {
                    AppId = row.AppId,
                    ExeName = app?.ExeName ?? $"#{row.AppId}",
                    DisplayName = app?.DisplayName ?? $"#{row.AppId}",
                    Seconds = Math.Round(row.Seconds, 1)
                });
            }
            hours.Add(dto);
        }
        payload.Hours = hours;

        // Топ приложений
        payload.TopApps = BuildTopApps(data);

        // Пользователи
        payload.PerUser = data.Usage
            .Where(u => u.Seconds > 0)
            .GroupBy(u => u.UserName)
            .Select(g => new DashboardUserDto
            {
                UserName = g.Key,
                Seconds = Math.Round(g.Sum(x => x.Seconds), 1),
                Percent = Percent(g.Sum(x => x.Seconds), payload.Totals.TrackedSeconds),
                SessionIds = g.Select(x => x.SessionId).Distinct().OrderBy(x => x).ToList()
            })
            .OrderByDescending(u => u.Seconds)
            .ToList();

        payload.Critical = data.Statuses.Select(MapStatus).ToList();
        payload.Events = _rules.ListEvents(dayKey, 100).Select(MapEvent).ToList();
        payload.Timeline = _activity.GetDayIntervals(dayKey, 600)
            .Select(i => new DashboardTimelineItemDto
            {
                Id = i.Id,
                AppId = i.AppId,
                ExeName = i.ExeName,
                DisplayName = i.DisplayName,
                UserName = i.UserName,
                SessionId = i.SessionId,
                StartUtc = i.StartUtc,
                EndUtc = i.EndUtc,
                Seconds = Math.Round(i.Seconds, 1),
                HourIndex = i.HourIndex
            })
            .ToList();
        payload.Agents = _agents?.GetAgentStatuses(true).ToList() ?? new List<AgentStatusDto>();

        return payload;
    }

    private List<DashboardAppDto> BuildTopApps(DayData data) => data.AppSeconds
        .OrderByDescending(kv => kv.Value)
        .Select(kv =>
        {
            data.Apps.TryGetValue(kv.Key, out var app);
            var rows = data.Usage.Where(u => u.AppId == kv.Key).ToList();
            var groups = data.GroupMembers.Where(g => g.Value.Contains(kv.Key)).Select(g => g.Key).ToList();
            return new DashboardAppDto
            {
                AppId = kv.Key,
                ExeName = app?.ExeName ?? $"#{kv.Key}",
                DisplayName = app?.DisplayName ?? $"#{kv.Key}",
                FriendlyName = app?.FriendlyName,
                ExePath = app?.ExePath,
                Seconds = Math.Round(kv.Value, 1),
                Percent = Percent(kv.Value, data.TrackedSeconds),
                SessionCount = rows.Select(r => r.SessionId).Distinct().Count(),
                UserNames = rows.Select(r => r.UserName).Distinct().OrderBy(x => x).ToList(),
                GroupIds = groups,
                GroupNames = groups.Select(g => data.Groups.TryGetValue(g, out var gr) ? gr.Name : $"#{g}").ToList(),
                Critical = PickLimit(data, kv.Key)
            };
        })
        .ToList();

    private static CriticalLimitDto? PickLimit(DayData data, long appId) => data.Statuses
        .Where(s => s.Enabled && s.ApplicableToday && s.LimitSeconds > 0 &&
                    (string.Equals(s.TargetType, "app", StringComparison.OrdinalIgnoreCase)
                        ? s.TargetId == appId
                        : data.GroupMembers.TryGetValue(s.TargetId, out var members) && members.Contains(appId)))
        .OrderByDescending(s => s.Percent)
        .Select(s => new CriticalLimitDto
        {
            RuleId = s.RuleId,
            RuleName = s.RuleName,
            LimitSeconds = s.LimitSeconds,
            UsedSeconds = Math.Round(s.UsedSeconds, 1),
            Percent = s.Percent,
            Exceeded = s.Exceeded,
            Severity = s.Severity
        })
        .FirstOrDefault();

    private static double Percent(double value, double total) =>
        total > 0 ? Math.Round(value / total * 100.0, 1) : 0;

    // ---------------------------------------------------------------- приложения

    public List<AppListItemDto> BuildApps(string dayKey, string? search, string? sort, TimeRange? range = null)
    {
        var data = Load(dayKey);
        var membersByApp = data.GroupMembers
            .SelectMany(kv => kv.Value.Select(appId => (AppId: appId, GroupId: kv.Key)))
            .GroupBy(x => x.AppId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.GroupId).ToList());

        // Период задан явно (в том числе now-1d): считаем время приложений по нему.
        var rangeSeconds = new Dictionary<long, double>();
        var rangeSessions = new Dictionary<long, HashSet<int>>();
        var rangeUsers = new Dictionary<long, HashSet<string>>();
        double rangeTotal = 0;
        if (range is not null)
        {
            foreach (var row in _activity.GetIntervalsInRange(range.FromUtc, range.ToUtc))
            {
                if (data.Apps.TryGetValue(row.AppId, out var rowApp) && rowApp.IsIgnored) continue;
                var seconds = row.SecondsInRange(range.FromUtc, range.ToUtc);
                if (seconds <= 0.01) continue;

                rangeTotal += seconds;
                rangeSeconds[row.AppId] = rangeSeconds.GetValueOrDefault(row.AppId) + seconds;
                if (!rangeSessions.TryGetValue(row.AppId, out var sessionSet))
                {
                    sessionSet = new HashSet<int>();
                    rangeSessions[row.AppId] = sessionSet;
                }
                sessionSet.Add(row.SessionId);
                if (!rangeUsers.TryGetValue(row.AppId, out var userSet))
                {
                    userSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    rangeUsers[row.AppId] = userSet;
                }
                userSet.Add(row.UserName);
            }
        }

        var items = data.Apps.Values.Select(app =>
        {
            var seconds = range is null
                ? (data.AppSeconds.TryGetValue(app.Id, out var v) ? v : 0)
                : rangeSeconds.GetValueOrDefault(app.Id);
            var total = range is null ? data.TrackedSeconds : rangeTotal;
            var rows = data.Usage.Where(u => u.AppId == app.Id).ToList();
            var groupIds = membersByApp.TryGetValue(app.Id, out var ids) ? ids : new List<long>();
            return new AppListItemDto
            {
                Id = app.Id,
                ExeName = app.ExeName,
                ExePath = app.ExePath,
                FriendlyName = app.FriendlyName,
                DisplayName = app.DisplayName,
                Category = app.Category,
                IsIgnored = app.IsIgnored,
                FirstSeenUtc = app.FirstSeenUtc,
                LastSeenUtc = app.LastSeenUtc,
                TodaySeconds = Math.Round(seconds, 1),
                PercentOfTracked = Percent(seconds, total),
                SessionCount = range is null
                    ? rows.Select(r => r.SessionId).Distinct().Count()
                    : rangeSessions.GetValueOrDefault(app.Id)?.Count ?? 0,
                UserNames = range is null
                    ? rows.Select(r => r.UserName).Distinct().OrderBy(x => x).ToList()
                    : (rangeUsers.GetValueOrDefault(app.Id) ?? new HashSet<string>()).OrderBy(x => x).ToList(),
                GroupIds = groupIds,
                GroupNames = groupIds.Select(g => data.Groups.TryGetValue(g, out var gr) ? gr.Name : $"#{g}").ToList(),
                Critical = PickLimit(data, app.Id)
            };
        });

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            items = items.Where(a =>
                a.ExeName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                a.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                (a.ExePath?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        items = (sort ?? "todaySeconds").ToLowerInvariant() switch
        {
            "name" => items.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            "lastseen" => items.OrderByDescending(a => a.LastSeenUtc),
            _ => items.OrderByDescending(a => a.TodaySeconds).ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

        return items.ToList();
    }

    // ---------------------------------------------------------------- группы

    public List<GroupDto> BuildGroups(string dayKey)
    {
        var data = Load(dayKey);
        return data.Groups.Values
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var members = data.GroupMembers.TryGetValue(group.Id, out var m) ? m : new List<long>();
                var status = data.Statuses
                    .Where(s => string.Equals(s.TargetType, "group", StringComparison.OrdinalIgnoreCase) && s.TargetId == group.Id)
                    .OrderByDescending(s => s.Percent)
                    .FirstOrDefault();
                return new GroupDto
                {
                    Id = group.Id,
                    Name = group.Name,
                    Description = group.Description,
                    Color = group.Color,
                    AppIds = members.ToList(),
                    AppCount = members.Count,
                    TodaySeconds = Math.Round(members.Sum(id => data.AppSeconds.TryGetValue(id, out var v) ? v : 0), 1),
                    Critical = status is null
                        ? null
                        : new CriticalLimitDto
                        {
                            RuleId = status.RuleId,
                            RuleName = status.RuleName,
                            LimitSeconds = status.LimitSeconds,
                            UsedSeconds = Math.Round(status.UsedSeconds, 1),
                            Percent = status.Percent,
                            Exceeded = status.Exceeded,
                            Severity = status.Severity
                        },
                    Apps = members.Select(id =>
                    {
                        data.Apps.TryGetValue(id, out var app);
                        return new GroupAppDto
                        {
                            Id = id,
                            ExeName = app?.ExeName ?? $"#{id}",
                            DisplayName = app?.DisplayName ?? $"#{id}",
                            TodaySeconds = Math.Round(data.AppSeconds.TryGetValue(id, out var v) ? v : 0, 1)
                        };
                    }).OrderByDescending(a => a.TodaySeconds).ToList()
                };
            })
            .ToList();
    }

    // ---------------------------------------------------------------- правила

    public List<CriticalRuleDto> BuildCriticalRules()
    {
        var apps = _activity.ListApplications().ToDictionary(a => a.Id);
        var groups = _activity.ListGroups().ToDictionary(g => g.Id);
        var members = _activity.ListGroupMembers().GroupBy(x => x.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.AppId).ToList());

        return _rules.ListCriticalRules().Select(rule =>
        {
            var target = _critical.ResolveTarget(rule, apps, groups, members);
            return new CriticalRuleDto
            {
                Id = rule.Id,
                Name = rule.Name,
                TargetType = rule.TargetType,
                TargetId = string.Equals(rule.TargetType, "group", StringComparison.OrdinalIgnoreCase)
                    ? rule.GroupId ?? 0
                    : rule.ApplicationId ?? 0,
                TargetName = target.Name,
                DailyLimitSeconds = rule.DailyLimitSeconds,
                Enabled = rule.Enabled,
                Severity = rule.Severity,
                DaysOfWeek = string.IsNullOrWhiteSpace(rule.DaysOfWeek)
                    ? null
                    : rule.DaysOfWeek.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList(),
                CreatedUtc = rule.CreatedUtc,
                UpdatedUtc = rule.UpdatedUtc
            };
        }).ToList();
    }

    public List<CriticalStatusDto> BuildCriticalStatuses(string dayKey) =>
        Load(dayKey).Statuses.Select(MapStatus).ToList();

    public static CriticalStatusDto MapStatus(CriticalStatus status) => new()
    {
        RuleId = status.RuleId,
        RuleName = status.RuleName,
        TargetType = status.TargetType,
        TargetId = status.TargetId,
        TargetName = status.TargetName,
        LimitSeconds = status.LimitSeconds,
        UsedSeconds = Math.Round(status.UsedSeconds, 1),
        Percent = status.Percent,
        Exceeded = status.Exceeded,
        Severity = status.Severity,
        Enabled = status.Enabled,
        ApplicableToday = status.ApplicableToday,
        FirstExceededUtc = status.FirstExceededUtc
    };

    public static WorkflowEventDto MapEvent(WorkflowEventRecord e) => new()
    {
        Id = e.Id,
        OccurredUtc = e.OccurredUtc,
        DayKey = e.DayKey,
        RuleId = e.SourceId,
        RuleName = e.RuleName,
        Severity = e.Severity,
        Message = e.Message,
        TargetName = e.TargetName,
        UsedSeconds = e.UsedSeconds,
        LimitSeconds = e.LimitSeconds,
        ActionType = e.ActionType,
        ActionStatus = e.ActionStatus
    };

    public static WorkflowRuleDto MapRule(WorkflowRuleRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Enabled = r.Enabled,
        TriggerType = r.TriggerType,
        CriticalRuleId = r.CriticalRuleId,
        ScheduleMinutes = r.ScheduleMinutes,
        TargetType = r.TargetType,
        TargetId = r.TargetId,
        ThresholdSeconds = r.ThresholdSeconds,
        ActionType = r.ActionType,
        ActionTarget = r.ActionTarget,
        CooldownMinutes = r.CooldownMinutes,
        LastRunUtc = r.LastRunUtc,
        LastStatus = r.LastStatus,
        TriggerCount = r.TriggerCount,
        CreatedUtc = r.CreatedUtc,
        UpdatedUtc = r.UpdatedUtc
    };

    public List<WorkflowRuleDto> BuildWorkflowRules() => _rules.ListWorkflowRules().Select(MapRule).ToList();

    public List<WorkflowEventDto> BuildEvents(string dayKey, int limit) =>
        _rules.ListEvents(dayKey, limit).Select(MapEvent).ToList();
}
