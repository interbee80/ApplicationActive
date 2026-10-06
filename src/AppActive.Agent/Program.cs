using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppActive.Agent;

/// <summary>
/// AppActive.Agent — процесс сеанса пользователя. Запускается службой ApplicationActive
/// в каждом активном сеансе, следит за активным окном и простоем ввода и отправляет
/// агрегированные отрезки активности службе (HTTP на localhost).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var parsed = AgentArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (parsed.SelfTest)
        {
            return AgentSelfTest.Run();
        }

        var config = AgentConfig.Load(parsed.ConfigPath, parsed.SessionId);
        AgentLog.Init(config.LogFile, parsed.Verbose ? "debug" : config.LogLevel, config.SessionId);
        AgentLog.Info($"Запуск: сеанс={config.SessionId}, пользователь={config.Domain}\\{config.UserName}, " +
                      $"служба={config.ServiceUrl}, интервал={config.PollIntervalSeconds} с, N={config.ActivityIdleSeconds} с, " +
                      $"заголовки окон={config.CollectWindowTitles}");

        using var watcher = new ForegroundWatcher();
        watcher.Start();
        using var reporter = new Reporter(config);
        using var sampler = new Sampler(config, watcher, reporter);

        if (parsed.RunOnce)
        {
            sampler.TickNow();
            return 0;
        }

        sampler.Start();

        var mainThreadId = GetCurrentThreadId();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => sampler.Stop();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            NativeMethods.PostThreadMessage(mainThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        };

        // Если служба недоступна слишком долго — выходим из цикла сообщений и завершаемся,
        // чтобы не остаться осиротевшим процессом в сеансе пользователя.
        sampler.FatalError += () => NativeMethods.PostThreadMessage(mainThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

        // Цикл сообщений нужен для доставки событий крючка EVENT_SYSTEM_FOREGROUND.
        while (true)
        {
            var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result <= 0) break;
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        sampler.Stop();
        return 0;
    }

    private const uint WmQuit = 0x0012;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static void PrintHelp()
    {
        Console.WriteLine("""
            AppActive.Agent — агент учёта активности приложений для одного сеанса Windows.

              --config <путь>   файл конфигурации, подготовленный службой (JSON)
              --session <id>    идентификатор сеанса Windows (по умолчанию — текущий)
              --once            выполнить один такт учёта и выйти (диагностика)
              --self-test       проверить правило учёта активности на синтетических данных
              --verbose         подробный журнал
              --help            эта справка
            """);
    }
}
