using AppActive.Core;
using AppActive.Core.Data;
using AppActive.Core.Models;
using AppActive.Core.Options;
using AppActive.Core.Services;
using AppActive.Core.Settings;

namespace AppActive.Service;

/// <summary>
/// Самопроверка ядра: схема базы, автосоздание приложений, разбиение времени по часам,
/// склейка и разрыв интервалов, групповые лимиты, события и агрегаты dashboard.
/// Запуск: AppActive.Service.exe --self-test
/// </summary>
internal static class SelfTest
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        Console.WriteLine("ApplicationActive — самопроверка ядра");
        var root = Path.Combine(Path.GetTempPath(), "appactive-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        try
        {
            var database = new SqliteDatabase(Path.Combine(root, "selftest.db"));
            database.Initialize();

            var activity = new ActivityStore(database);
            var rulesStore = new RulesStore(database);
            var settingsService = new SettingsService(new SettingsStore(database));
            var accumulator = new ActivityAccumulator(activity, () => settingsService.Current);
            var evaluator = new CriticalEvaluator(rulesStore, activity, settingsService);
            var workflow = new WorkflowEngine(rulesStore, activity, evaluator, new AppActiveOptions());
            var query = new DashboardQueryService(activity, rulesStore, evaluator, settingsService);

            // Отметки времени берём за вчерашний день, чтобы они гарантированно были в прошлом.
            var testDay = TimeUtil.ShiftDayKey(TimeUtil.TodayKey(), -1);
            var hourStart = TimeUtil.DayStartUtc(testDay).AddHours(10);
            var hourEnd = hourStart.AddHours(1);
            var now = DateTime.UtcNow;

            Check("Схема SQLite создана (таблицы приложений нет — пустая база)", activity.CountApplications() == 0);

            var appA = activity.EnsureApplication(ActivityStore.BuildAppKey("chrome.exe", @"C:\chrome.exe"), "chrome.exe", @"C:\chrome.exe", now);
            var appAAgain = activity.EnsureApplication(ActivityStore.BuildAppKey("chrome.exe", @"C:\chrome.exe"), "chrome.exe", @"C:\chrome.exe", now);
            var appB = activity.EnsureApplication(ActivityStore.BuildAppKey("excel.exe", @"C:\excel.exe"), "excel.exe", @"C:\excel.exe", now);
            Check("Приложение создаётся автоматически и не дублируется", appA.Id > 0 && appA.Id == appAAgain.Id && appB.Id != appA.Id);
            Check("В базе ровно два приложения", activity.CountApplications() == 2);

            // Отрезок 60 с, начинающийся за 30 с до конца часа: должен распасться на два часа.
            var spanningStart = hourEnd.AddSeconds(-30);
            accumulator.Add(appA.Id, 1, "tester", spanningStart, 60, null, now);
            accumulator.Flush();

            var intervals = activity.GetIntervalsForApp(appA.Id, testDay);
            Check("Отрезок разбит по границе часа (2 записи)", intervals.Count == 2);
            Check("Суммарная длительность сохранена (60 с)",
                Math.Abs(intervals.Sum(i => i.Seconds) - 60) < 1.0);
            Check("Записи попали в разные часы", intervals.Select(i => i.HourIndex).Distinct().Count() == 2);
            Check("Активное время приложения за сутки = 60 с",
                Math.Abs(activity.GetAppSecondsForDay(testDay).GetValueOrDefault(appA.Id) - 60) < 1.0);

            // Небольшой промежуток (10 с) — интервал продлевается той же записью.
            accumulator.Add(appA.Id, 1, "tester", hourEnd.AddSeconds(5), 10, null, now);
            accumulator.Flush();
            var afterExtend = activity.GetIntervalsForApp(appA.Id, testDay);
            Check("Короткий промежуток продлевает существующий интервал", afterExtend.Count == 2);

            // Долгий промежуток (30 мин) — начинается новый интервал.
            accumulator.Add(appA.Id, 1, "tester", hourEnd.AddMinutes(30), 20, null, now);
            accumulator.Flush();
            var afterGap = activity.GetIntervalsForApp(appA.Id, testDay);
            Check("Разрыв больше допустимого создаёт новый интервал", afterGap.Count == 3);
            Check("Сумма по приложению = 90 с",
                Math.Abs(activity.GetAppSecondsForDay(testDay).GetValueOrDefault(appA.Id) - 90) < 1.0);

            // Группа и групповой лимит.
            var groupId = activity.CreateGroup("Тестовая группа", "проверка", "#4f8cff");
            activity.SetGroupMembers(groupId, new[] { appA.Id, appB.Id });
            accumulator.Add(appB.Id, 1, "tester", hourEnd.AddMinutes(35), 90, null, now);
            accumulator.Flush();

            var ruleId = rulesStore.CreateCriticalRule(new CriticalRuleRecord
            {
                Name = "Групповой лимит 100 с",
                TargetType = "group",
                GroupId = groupId,
                DailyLimitSeconds = 100,
                Enabled = true,
                Severity = "critical",
                CreatedUtc = TimeUtil.ToIso(now),
                UpdatedUtc = TimeUtil.ToIso(now)
            });

            var statuses = evaluator.Evaluate(testDay);
            var status = statuses.First(s => s.RuleId == ruleId);
            Check($"Время группы суммируется по приложениям ({status.UsedSeconds:0} с)", status.UsedSeconds >= 170);
            Check("Превышение критического лимита обнаружено", status.Exceeded);
            Check("Цель правила подписана понятным именем", status.TargetName.Contains("Тестовая группа"));

            var firstEvents = evaluator.EmitExceededEvents(testDay, statuses);
            var secondEvents = evaluator.EmitExceededEvents(testDay, statuses);
            Check("Событие превышения записано один раз (без дублей)", firstEvents.Count == 1 && secondEvents.Count == 0);
            Check("Время первого превышения доступно для отчёта",
                (evaluator.Evaluate(testDay).First(s => s.RuleId == ruleId).FirstExceededUtc ?? "").Length > 0);

            // Workflow: реакция на превышение.
            var workflowRuleId = rulesStore.CreateWorkflowRule(new WorkflowRuleRecord
            {
                Name = "Проверка реакции",
                Enabled = true,
                TriggerType = "criticalExceeded",
                CriticalRuleId = ruleId,
                ActionType = "event",
                CooldownMinutes = 60,
                CreatedUtc = TimeUtil.ToIso(now),
                UpdatedUtc = TimeUtil.ToIso(now)
            });
            var run = workflow.Run(testDay, force: true);
            Check("Workflow выполнил действие по правилу", run.Actions.Any(a => a.RuleName == "Проверка реакции"));
            Check("Правило реакции отметило запуск", rulesStore.GetWorkflowRule(workflowRuleId)?.LastRunUtc is not null);
            Check("Повторный запуск не дублирует событие превышения",
                workflow.Run(testDay, force: true).EventsCreated == 0);

            // Dashboard.
            var dashboard = query.BuildDashboard(testDay);
            Check("Dashboard содержит 24 часа", dashboard.Hours.Count == 24);
            Check("Dashboard содержит оба приложения", dashboard.TopApps.Count >= 2);
            Check("Сумма времени в dashboard совпадает с базой",
                Math.Abs(dashboard.Totals.TrackedSeconds - dashboard.TopApps.Sum(a => a.Seconds)) < 1.0);
            Check("Часы в dashboard разложены по 10 и 11 часам",
                dashboard.Hours.First(h => h.Hour == 10).TotalSeconds > 0 &&
                dashboard.Hours.First(h => h.Hour == 11).TotalSeconds > 0);
            Check("Критическое время видно в dashboard", dashboard.Critical.Any(c => c.RuleId == ruleId && c.Exceeded));

            var appList = query.BuildApps(testDay, null, null);
            var chrome = appList.First(a => a.Id == appA.Id);
            Check("Список приложений содержит группу и лимит",
                chrome.GroupNames.Count > 0 && chrome.Critical is not null);

            // Цвета приложений: одинаковый цвет во всех блоках панели.
            Check("Приложениям назначены цвета палитры",
                dashboard.AppColors.Count >= 2 &&
                dashboard.AppColors.Values.All(c => c.StartsWith('#')) &&
                dashboard.AppColors.Values.Distinct().Count() == dashboard.AppColors.Count);

            // ---------------------------------------------------------- периоды

            Check("Период по умолчанию — сутки указанной даты",
                TimeRangeParser.TryParse(null, null, testDay, out var dayRange, out _) &&
                Math.Abs(dayRange.TotalSeconds - 86400) < 1);

            Check("Относительный период now-1h … now",
                TimeRangeParser.TryParse("now-1h", "now", null, out var hourRange, out _) &&
                Math.Abs(hourRange.TotalSeconds - 3600) < 5);

            Check("Составная длительность 1h30m",
                TimeRangeParser.TryParse("now-1h30m", "now", null, out var composedRange, out _) &&
                Math.Abs(composedRange.TotalSeconds - 5400) < 5);

            Check("Дата и время начала и конца периода",
                TimeRangeParser.TryParse("2026-10-06T10:30", "2026-10-06 12:00", null, out var exactRange, out _) &&
                exactRange.TotalSeconds >= 5400 && exactRange.TotalSeconds <= 5460);

            Check("Некорректный период отклоняется",
                !TimeRangeParser.TryParse("вчера", "сегодня", null, out _, out var rangeError) &&
                rangeError.Length > 0);

            Check("Слишком длинный период отклоняется",
                !TimeRangeParser.TryParse("now-400d", "now", null, out _, out _));

            Check("now+1h принимается (период в будущем)",
                TimeRangeParser.TryParse("now", "now+1h", null, out var futureRange, out _) &&
                futureRange.ToUtc > DateTime.UtcNow);

            // Период внутри суток: считаются только секунды, попавшие в него.
            var hour10 = TimeUtil.DayStartUtc(testDay).AddHours(10);
            Check("Конец периода с точностью до минуты включается целиком",
                TimeRangeParser.TryParse(
                    TimeUtil.ToLocal(hour10).ToString("yyyy-MM-ddTHH:mm"),
                    TimeUtil.ToLocal(hour10.AddHours(1)).ToString("yyyy-MM-ddTHH:mm"),
                    null, out var minuteRange, out _) &&
                minuteRange.TotalSeconds >= 3600 && minuteRange.TotalSeconds <= 3660);

            Check("Период одного часа принимается парсером",
                TimeRangeParser.TryParse(
                    TimeUtil.ToLocal(hour10).ToString("yyyy-MM-ddTHH:mm:ss"),
                    TimeUtil.ToLocal(hour10.AddHours(1)).ToString("yyyy-MM-ddTHH:mm:ss"),
                    null, out var oneHour, out _) &&
                Math.Abs(oneHour.TotalSeconds - 3600) < 1);

            var rangeDashboard = query.BuildDashboard(testDay, oneHour);
            var dayTotal = dashboard.Totals.TrackedSeconds;
            Check("За часовой период времени меньше, чем за сутки",
                rangeDashboard.Totals.TrackedSeconds > 0 && rangeDashboard.Totals.TrackedSeconds < dayTotal);
            Check("Часовой период даёт столбцы по часам",
                rangeDashboard.Range.BucketKind == "hour" && rangeDashboard.Buckets.Count == 1);
            Check("В часовом периоде видны приложения",
                rangeDashboard.TopApps.Count > 0 && rangeDashboard.TopApps.Sum(a => a.Seconds) > 0);

            var weekRange = new TimeRange
            {
                FromUtc = TimeUtil.DayStartUtc(TimeUtil.ShiftDayKey(testDay, -6)),
                ToUtc = TimeUtil.DayEndUtc(testDay),
                FromText = "now-7d",
                ToText = "now"
            };
            var weekDashboard = query.BuildDashboard(testDay, weekRange);
            Check("Длинный период даёт столбцы по суткам",
                weekDashboard.Range.BucketKind == "day" && weekDashboard.Buckets.Count == 7);
            Check("Длинный период включает активность за сутки с данными",
                Math.Abs(weekDashboard.Totals.TrackedSeconds - dayTotal) < 1.0);

            var rangeApps = query.BuildApps(testDay, null, null, oneHour);
            Check("Список приложений считается по периоду",
                rangeApps.Any(a => a.TodaySeconds > 0) &&
                rangeApps.Sum(a => a.TodaySeconds) < appList.Sum(a => a.TodaySeconds) + 1);

            // Настройки.
            var updated = settingsService.Update(s => s.PollIntervalSeconds = 42);
            var reloaded = new SettingsService(new SettingsStore(database)).Current;
            Check("Настройки сохраняются в базу и читаются заново",
                updated.PollIntervalSeconds == 42 && reloaded.PollIntervalSeconds == 42);
            Check("Настройки ограничивают некорректные значения",
                settingsService.Update(s => s.PollIntervalSeconds = 100000).PollIntervalSeconds == 3600);

            // Очистка истории.
            Check("Очистка по retention удаляет старые интервалы",
                activity.PurgeBefore(TimeUtil.ShiftDayKey(testDay, 1)) == 4);
        }
        catch (Exception ex)
        {
            Check("Самопроверка завершилась без исключений: " + ex.Message, false);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* временный каталог останется */ }
        }

        Console.WriteLine($"Итог: успешно {_passed}, ошибок {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string title, bool ok)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  [OK]   " + title);
        }
        else
        {
            _failed++;
            Console.WriteLine("  [FAIL] " + title);
        }
    }
}
