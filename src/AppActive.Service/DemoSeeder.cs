using AppActive.Core;
using AppActive.Core.Data;
using AppActive.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AppActive.Service;

/// <summary>Наполняет базу правдоподобными данными — удобно для проверки Web-панели без ожидания суток.</summary>
internal static class DemoSeeder
{
    private sealed record DemoApp(string ExeName, string ExePath, string? FriendlyName, string? Category, double Weight);

    private static readonly DemoApp[] Catalog =
    {
        new("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Chrome", "Браузеры", 5.0),
        new("msedge.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Edge", "Браузеры", 3.0),
        new("EXCEL.EXE", @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE", "Excel", "Офис", 3.5),
        new("WINWORD.EXE", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", "Word", "Офис", 2.0),
        new("1cv8.exe", @"C:\Program Files\1cv8\common\1cestart.exe", "1С:Предприятие", "Учёт", 4.5),
        new("telegram.exe", @"C:\Users\Public\Telegram\Telegram.exe", "Telegram", "Мессенджеры", 2.5),
        new("Code.exe", @"C:\Program Files\Microsoft VS Code\Code.exe", "VS Code", "Разработка", 2.0),
        new("explorer.exe", @"C:\Windows\explorer.exe", "Проводник", "Система", 1.5)
    };

    public static int Seed(IServiceProvider services, int days)
    {
        var activity = services.GetRequiredService<ActivityStore>();
        var rulesStore = services.GetRequiredService<RulesStore>();
        var database = services.GetRequiredService<SqliteDatabase>();
        var random = new Random(20260201 + days);

        var userName = Environment.UserName;
        var sessionId = Environment.ProcessId > 0 ? System.Diagnostics.Process.GetCurrentProcess().SessionId : 1;
        var now = DateTime.UtcNow;

        var apps = new List<(long Id, double Weight, string Exe, string Display)>();
        foreach (var item in Catalog)
        {
            var key = ActivityStore.BuildAppKey(item.ExeName, item.ExePath);
            var record = activity.EnsureApplication(key, item.ExeName, item.ExePath, now);
            if (item.FriendlyName is not null || item.Category is not null)
            {
                activity.UpdateApplication(record.Id, item.FriendlyName, item.FriendlyName is not null,
                    item.Category, item.Category is not null, null);
            }
            apps.Add((record.Id, item.Weight, item.ExeName, item.FriendlyName ?? item.ExeName));
        }

        var written = 0;
        var today = TimeUtil.TodayKey();
        var currentHour = TimeUtil.HourIndex(now);

        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            for (var offset = days - 1; offset >= 0; offset--)
            {
                var dayKey = TimeUtil.ShiftDayKey(today, -offset);
                var dayStart = TimeUtil.DayStartUtc(dayKey);
                var lastHour = offset == 0 ? Math.Min(19, currentHour) : 19;

                for (var hour = 8; hour <= lastHour; hour++)
                {
                    var cursor = dayStart.AddHours(hour);
                    var hourEnd = cursor.AddHours(1);

                    while (cursor < hourEnd)
                    {
                        var pick = Pick(apps, random);
                        var remaining = (hourEnd - cursor).TotalSeconds;
                        if (remaining < 60) break;

                        var seconds = Math.Round(Math.Min(random.Next(120, 2100), remaining) * (0.65 + random.NextDouble() * 0.35));
                        if (seconds < 30) break;

                        activity.InsertInterval(connection, transaction, pick.Id, sessionId, userName, dayKey, hour,
                            cursor, cursor.AddSeconds(seconds), seconds, null);
                        written++;
                        cursor = cursor.AddSeconds(seconds + random.Next(5, 300));
                    }
                }
            }
            transaction.Commit();
        }

        SeedGroups(activity, rulesStore, apps, days);
        return written;
    }

    private static (long Id, double Weight, string Exe, string Display) Pick(
        List<(long Id, double Weight, string Exe, string Display)> apps, Random random)
    {
        var total = apps.Sum(a => a.Weight);
        var target = random.NextDouble() * total;
        foreach (var app in apps)
        {
            target -= app.Weight;
            if (target <= 0) return app;
        }
        return apps[0];
    }

    private static void SeedGroups(ActivityStore activity, RulesStore rulesStore,
        List<(long Id, double Weight, string Exe, string Display)> apps, int days)
    {
        if (activity.ListGroups().Count > 0 || rulesStore.ListCriticalRules().Count > 0) return;

        long Find(string exe) => apps.FirstOrDefault(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase)).Id;

        var browsers = activity.CreateGroup("Браузеры", "Интернет-браузеры: рабочие и личные", "#4f8cff");
        activity.SetGroupMembers(browsers, new[] { Find("chrome.exe"), Find("msedge.exe") });

        var office = activity.CreateGroup("Офис", "Документы, таблицы и бухгалтерия", "#3fb27f");
        activity.SetGroupMembers(office, new[] { Find("EXCEL.EXE"), Find("WINWORD.EXE"), Find("1cv8.exe") });

        var messengers = activity.CreateGroup("Мессенджеры", "Личные коммуникации", "#e0a33a");
        activity.SetGroupMembers(messengers, new[] { Find("telegram.exe") });

        var now = TimeUtil.ToIso(DateTime.UtcNow);
        var chromeRule = rulesStore.CreateCriticalRule(new CriticalRuleRecord
        {
            Name = "Chrome: не больше 2 часов в сутки",
            TargetType = "app",
            ApplicationId = Find("chrome.exe"),
            DailyLimitSeconds = 7200,
            Enabled = true,
            Severity = "warning",
            DaysOfWeek = "1,2,3,4,5",
            CreatedUtc = now,
            UpdatedUtc = now
        });

        rulesStore.CreateCriticalRule(new CriticalRuleRecord
        {
            Name = "Браузеры: не больше 3 часов",
            TargetType = "group",
            GroupId = browsers,
            DailyLimitSeconds = 10800,
            Enabled = true,
            Severity = "critical",
            CreatedUtc = now,
            UpdatedUtc = now
        });

        rulesStore.CreateCriticalRule(new CriticalRuleRecord
        {
            Name = "Мессенджеры: не больше 1 часа",
            TargetType = "group",
            GroupId = messengers,
            DailyLimitSeconds = 3600,
            Enabled = true,
            Severity = "warning",
            CreatedUtc = now,
            UpdatedUtc = now
        });

        rulesStore.CreateWorkflowRule(new WorkflowRuleRecord
        {
            Name = "Записать событие при превышении лимита Chrome",
            Enabled = true,
            TriggerType = "criticalExceeded",
            CriticalRuleId = chromeRule,
            ActionType = "event",
            CooldownMinutes = 60,
            CreatedUtc = now,
            UpdatedUtc = now
        });
    }
}
