using System.Diagnostics;
using AppActive.Core;
using AppActive.Core.Data;
using AppActive.Core.Models;

namespace AppActive.Service.Windows;

/// <summary>
/// Список процессов приложения в системе и их завершение.
/// Служба работает от имени SYSTEM, поэтому может завершать процессы любого пользовательского сеанса.
/// Системные процессы защищены от завершения.
/// </summary>
internal sealed class ProcessManager
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "registry", "memory compression", "secure system",
        "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "smss.exe",
        "svchost.exe", "dwm.exe", "fontdrvhost.exe", "audiodg.exe", "sihost.exe", "wudfhost.exe",
        "msmpeng.exe", "securityhealthservice.exe", "appactive.service.exe", "appactive.agent.exe"
    };

    private readonly RulesStore _rules;
    private static bool _privilegesPrepared;

    public ProcessManager(RulesStore rules) => _rules = rules;

    public sealed class KillOutcome
    {
        public List<ProcessKillResultDto> Results { get; } = new();
        public int KilledCount => Results.Count(r => r.Status == "killed");
        public int Error { get; set; }
    }

    /// <summary>Находит все процессы указанного приложения (по имени и, если известно, по полному пути).</summary>
    public List<ColorizedProcessDto> ListForApplication(ApplicationRecord app)
    {
        var result = new List<ColorizedProcessDto>();
        foreach (var process in FindProcesses(app))
        {
            result.Add(Describe(process, app));
        }

        return result.OrderBy(p => p.SessionId).ThenBy(p => p.Pid).ToList();
    }

    private static IEnumerable<Process> FindProcesses(ApplicationRecord app)
    {
        var name = Path.GetFileNameWithoutExtension(app.ExeName);
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<Process>();

        var candidates = new List<Process>();
        try
        {
            candidates.AddRange(Process.GetProcessesByName(name));
        }
        catch
        {
            return Array.Empty<Process>();
        }

        if (string.IsNullOrWhiteSpace(app.ExePath)) return candidates;

        // Если известен путь, оставляем только совпадающие процессы; недоступные пути не отбрасываем.
        var filtered = new List<Process>();
        foreach (var process in candidates)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(path) || string.Equals(path, app.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(process);
                }
            }
            catch
            {
                filtered.Add(process);
            }
        }

        return filtered.Count > 0 ? filtered : candidates;
    }

    private static ColorizedProcessDto Describe(Process process, ApplicationRecord app)
    {
        var name = SafeName(process);
        var isProtected = ProtectedNames.Contains(name);
        DateTime? started = null;
        try
        {
            started = process.StartTime.ToUniversalTime();
        }
        catch
        {
            // время запуска недоступно
        }

        double memoryMb = 0;
        try
        {
            memoryMb = process.WorkingSet64 / 1024.0 / 1024.0;
        }
        catch
        {
            // память недоступна
        }

        int sessionId = -1;
        try
        {
            sessionId = process.SessionId;
        }
        catch
        {
            // сеанс недоступен
        }

        var title = "";
        try
        {
            title = process.MainWindowTitle;
        }
        catch
        {
            // заголовок недоступен
        }

        return new ColorizedProcessDto
        {
            Pid = process.Id,
            ExeName = name,
            ExePath = app.ExePath,
            SessionId = sessionId,
            StartedUtc = started.HasValue ? TimeUtil.ToIso(started.Value) : null,
            MemoryMb = Math.Round(memoryMb, 1),
            WindowTitle = title ?? "",
            IsProtected = isProtected,
            ProtectedReason = isProtected ? "системный процесс или процесс самой службы — завершение запрещено" : null
        };
    }

    /// <summary>
    /// Завершает процессы приложения. Если указан <paramref name="pid"/> — только его,
    /// иначе все найденные процессы приложения во всех сеансах.
    /// </summary>
    public KillOutcome Kill(ApplicationRecord app, int? pid, string? reason, string initiatedBy)
    {
        if (!_privilegesPrepared)
        {
            _privilegesPrepared = true;
            Privileges.Enable("SeDebugPrivilege");
        }

        var outcome = new KillOutcome();
        var targets = new List<Process>();

        if (pid.HasValue)
        {
            try
            {
                targets.Add(Process.GetProcessById(pid.Value));
            }
            catch (ArgumentException)
            {
                outcome.Results.Add(new ProcessKillResultDto
                {
                    Pid = pid.Value,
                    ExeName = app.ExeName,
                    Status = "notFound",
                    Message = "процесс уже завершён"
                });
                return outcome;
            }
        }
        else
        {
            targets.AddRange(FindProcesses(app));
        }

        foreach (var process in targets)
        {
            var dto = new ProcessKillResultDto
            {
                Pid = process.Id,
                ExeName = SafeName(process),
                SessionId = SafeSession(process)
            };

            if (ProtectedNames.Contains(dto.ExeName))
            {
                dto.Status = "protected";
                dto.Message = "системный процесс или процесс службы — завершение запрещено";
                outcome.Results.Add(dto);
                continue;
            }

            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                dto.Status = "killed";
                dto.Message = process.HasExited ? null : "команда отправлена, процесс не завершился за 5 с";
            }
            catch (Exception ex)
            {
                dto.Status = "failed";
                dto.Message = ex.Message;
            }

            outcome.Results.Add(dto);
        }

        // Журналируем действие: оно видно в ленте событий панели.
        var killed = outcome.Results.Where(r => r.Status == "killed").Select(r => r.Pid).ToList();
        var message = killed.Count > 0
            ? $"Завершены процессы {app.DisplayName} ({app.ExeName}): PID {string.Join(", ", killed)}"
            : $"Завершение процессов {app.DisplayName} ({app.ExeName}): завершённых процессов нет";
        if (!string.IsNullOrWhiteSpace(reason)) message += $". Причина: {reason}";

        try
        {
            _rules.InsertEvent(new WorkflowEventRecord
            {
                OccurredUtc = TimeUtil.ToIso(DateTime.UtcNow),
                DayKey = TimeUtil.TodayKey(),
                SourceType = "system",
                SourceId = app.Id,
                RuleName = "Завершение процесса",
                Severity = killed.Count > 0 ? "warning" : "info",
                Message = message,
                TargetName = $"{app.DisplayName} ({app.ExeName})",
                ActionType = "killProcess",
                ActionStatus = killed.Count > 0 ? "ok" : "skipped",
                DedupKey = null
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось записать событие о завершении процесса: {ex.Message}");
        }

        AppLog.Warn($"Оператор {initiatedBy} завершил процесс(ы) {app.ExeName}: " +
                    $"успешно {outcome.KilledCount}, ошибок {outcome.Results.Count(r => r.Status == "failed")}");

        return outcome;
    }

    private static string SafeName(Process process)
    {
        try
        {
            var name = process.ProcessName;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        }
        catch
        {
            return "unknown.exe";
        }
    }

    private static int SafeSession(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch
        {
            return -1;
        }
    }
}
