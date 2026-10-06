namespace AppActive.Service;

/// <summary>Разбор аргументов командной строки службы.</summary>
internal sealed class CliOptions
{
    public const string ModeAuto = "auto";
    public const string ModeConsole = "console";
    public const string ModeService = "service";
    public const string ModeSelfTest = "self-test";
    public const string ModeSeedDemo = "seed-demo";

    public string Mode { get; private set; } = ModeAuto;

    public string? DataDirectory { get; private set; }

    public string? Urls { get; private set; }

    public int? Port { get; private set; }

    public bool NoMonitor { get; private set; }

    public int SeedDays { get; private set; } = 7;

    public bool ShowHelp { get; private set; }

    public List<string> Passthrough { get; } = new();

    public static CliOptions Parse(string[] args)
    {
        var result = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--console":
                case "-c":
                    result.Mode = ModeConsole;
                    break;
                case "--service":
                    result.Mode = ModeService;
                    break;
                case "--self-test":
                    result.Mode = ModeSelfTest;
                    break;
                case "--seed-demo":
                    result.Mode = ModeSeedDemo;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var days))
                    {
                        result.SeedDays = Math.Clamp(days, 1, 90);
                        i++;
                    }
                    break;
                case "--data-dir":
                    if (i + 1 < args.Length) result.DataDirectory = args[++i];
                    break;
                case "--urls":
                    if (i + 1 < args.Length) result.Urls = args[++i];
                    break;
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var port)) result.Port = port;
                    break;
                case "--no-monitor":
                    result.NoMonitor = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    result.ShowHelp = true;
                    break;
                default:
                    result.Passthrough.Add(arg);
                    break;
            }
        }

        return result;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            ApplicationActive — служба учёта активности приложений в сеансах Windows.

            Использование:
              AppActive.Service.exe [режим] [параметры]

            Режимы:
              --service            запуск как служба Windows (используется SCM, режим по умолчанию)
              --console            запуск в консоли от имени текущего пользователя (для отладки)
              --self-test          самопроверка базы, накопителя времени и правил (без запуска Web)
              --seed-demo [дни]    заполнить базу демонстрационными данными (по умолчанию 7 суток)

            Параметры:
              --data-dir <путь>    каталог данных (по умолчанию %ProgramData%\AppActive)
              --urls <адреса>      адреса Kestrel, например http://0.0.0.0:5090
              --port <номер>       порт Web-панели (эквивалент --urls http://127.0.0.1:порт)
              --no-monitor         не запускать агентов (только Web и REST)
              --help               эта справка

            Настройки читаются из appsettings.json рядом с исполняемым файлом,
            переменные окружения имеют префикс AppActive__, например:
              set AppActive__Urls__0=http://0.0.0.0:5090
            """);
    }
}
