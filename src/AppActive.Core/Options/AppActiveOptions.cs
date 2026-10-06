namespace AppActive.Core.Options;

/// <summary>
/// Корневая конфигурация (секция "AppActive" в appsettings.json рядом с исполняемым файлом,
/// либо переменные окружения AppActive__*).
///
/// В коде НЕТ значений по умолчанию для адресов, каталогов и имён службы: всё задаётся в appsettings.json,
/// а при отсутствии обязательного параметра служба останавливается с понятным сообщением
/// (см. AppActiveOptions.Validate). Так исключены расхождения «в настройке одно — работает другое».
/// </summary>
public sealed class AppActiveOptions
{
    /// <summary>Каталог данных (база, журналы, конфигурации агентов). Поддерживает %ПЕРЕМЕННЫЕ% окружения.</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>Имя файла базы SQLite внутри каталога данных.</summary>
    public string DatabaseFileName { get; set; } = "";

    /// <summary>Адреса Kestrel, например ["http://127.0.0.1:5190"] или ["http://0.0.0.0:5190"].</summary>
    public string[] Urls { get; set; } = Array.Empty<string>();

    /// <summary>Имя службы в диспетчере служб Windows.</summary>
    public string ServiceName { get; set; } = "";

    public string ServiceDisplayName { get; set; } = "";

    public string ServiceDescription { get; set; } = "";

    public SecurityOptions Security { get; set; } = new();

    public MonitoringOptions Monitoring { get; set; } = new();

    public WorkflowOptions Workflow { get; set; } = new();

    public LoggingOptions Logging { get; set; } = new();

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string AgentDirectory => Path.Combine(DataDirectory, "agents");

    /// <summary>Раскрывает %ПЕРЕМЕННЫЕ% окружения и приводит значения к каноническому виду.</summary>
    public void Normalize()
    {
        DataDirectory = Environment.ExpandEnvironmentVariables(DataDirectory ?? "").Trim();
        DatabaseFileName = (DatabaseFileName ?? "").Trim();

        if (Urls is null) Urls = Array.Empty<string>();
        Urls = Urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim().TrimEnd('/'))
            .ToArray();

        ServiceName = (ServiceName ?? "").Trim();
        ServiceDisplayName = (ServiceDisplayName ?? "").Trim();
        ServiceDescription = (ServiceDescription ?? "").Trim();
        Security.AdminKey = (Security.AdminKey ?? "").Trim();
        Security.IngestKey = (Security.IngestKey ?? "").Trim();
        Monitoring.AgentExecutable = (Monitoring.AgentExecutable ?? "").Trim();
        Logging.Level = (Logging.Level ?? "").Trim();
        Monitoring.Initial.Normalize();
    }

    /// <summary>Список проблем конфигурации. Пустой список — всё задано корректно.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            problems.Add("AppActive:DataDirectory — не задан каталог данных, например \"%ProgramData%\\\\AppActive\"");
        }
        if (string.IsNullOrWhiteSpace(DatabaseFileName))
        {
            problems.Add("AppActive:DatabaseFileName — не задано имя файла базы, например \"activity.db\"");
        }
        if (Urls.Length == 0)
        {
            problems.Add("AppActive:Urls — не задан адрес Web-панели, например [\"http://127.0.0.1:5190\"]");
        }
        foreach (var url in Urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                problems.Add($"AppActive:Urls — некорректный адрес '{url}', ожидается http://хост:порт");
            }
            else if (uri.Port <= 0)
            {
                problems.Add($"AppActive:Urls — в адресе '{url}' не указан порт");
            }
        }
        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            problems.Add("AppActive:ServiceName — не задано имя службы, например \"AppActive\"");
        }
        if (Monitoring.SessionScanSeconds <= 0)
        {
            problems.Add("AppActive:Monitoring:SessionScanSeconds — должно быть больше нуля");
        }
        if (Monitoring.AgentStaleSeconds <= 0)
        {
            problems.Add("AppActive:Monitoring:AgentStaleSeconds — должно быть больше нуля");
        }
        if (Workflow.EvaluationSeconds <= 0)
        {
            problems.Add("AppActive:Workflow:EvaluationSeconds — должно быть больше нуля");
        }
        if (Logging.FileRetentionDays <= 0)
        {
            problems.Add("AppActive:Logging:FileRetentionDays — должно быть больше нуля");
        }

        problems.AddRange(Monitoring.Initial.Validate());
        return problems;
    }
}

public sealed class SecurityOptions
{
    /// <summary>Ключ для операций изменения из Web-панели. Пусто = изменения разрешены только с loopback.</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>Ключ агентов (ingest). Пусто = генерируется автоматически при первом запуске.</summary>
    public string IngestKey { get; set; } = "";

    /// <summary>Разрешить чтение API с других машин (не только с loopback).</summary>
    public bool AllowRemoteRead { get; set; } = true;
}

public sealed class MonitoringOptions
{
    /// <summary>Следить ли за сеансами вообще.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Как часто сканировать список сеансов Windows, сек.</summary>
    public int SessionScanSeconds { get; set; } = 10;

    /// <summary>Запускать агентов в пользовательских сеансах.</summary>
    public bool LaunchAgents { get; set; } = true;

    /// <summary>Имя файла агента сеанса (ищется рядом со службой и в подкаталоге agent).</summary>
    public string AgentExecutable { get; set; } = "";

    /// <summary>Разрешить следить за собственным сеансом (нужно для запуска в режиме --console без прав SYSTEM).</summary>
    public bool AllowSelfSessionFallback { get; set; } = true;

    /// <summary>Агент считается зависшим, если не отчитывался дольше этого времени, сек.</summary>
    public int AgentStaleSeconds { get; set; } = 180;

    /// <summary>Собирать заголовки окон (значение для новых баз; дальше меняется из Web-панели).</summary>
    public bool CollectWindowTitles { get; set; } = true;

    /// <summary>Начальные значения параметров учёта — используются только при создании базы.</summary>
    public MonitoringInitialOptions Initial { get; set; } = new();
}

/// <summary>
/// Начальные значения параметров учёта. Применяются один раз — при создании базы данных;
/// дальше действуют значения, изменённые из Web-панели.
/// </summary>
public sealed class MonitoringInitialOptions
{
    /// <summary>Интервал опроса активного окна агентом, сек.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>N: приложение активно, если в течение N секунд была активность клавиатуры или мыши.</summary>
    public int ActivityIdleSeconds { get; set; } = 60;

    /// <summary>Как часто служба сбрасывает накопленные секунды в SQLite, сек.</summary>
    public int FlushSeconds { get; set; } = 15;

    /// <summary>Сколько суток хранить историю активности.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Не реже какого времени агент отчитывается даже без активности, сек.</summary>
    public int AgentHeartbeatSeconds { get; set; } = 60;

    public bool MonitorEnabled { get; set; } = true;

    public bool CollectWindowTitles { get; set; } = true;

    /// <summary>Процессы, которые не учитываются (например logonui.exe).</summary>
    public string[] IgnoreProcesses { get; set; } = Array.Empty<string>();

    public void Normalize()
    {
        if (IgnoreProcesses is null) IgnoreProcesses = Array.Empty<string>();
        IgnoreProcesses = IgnoreProcesses
            .Where(process => !string.IsNullOrWhiteSpace(process))
            .Select(process => process.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
    }

    public List<string> Validate()
    {
        var problems = new List<string>();

        void Range(string name, int value, int min, int max)
        {
            if (value < min || value > max)
            {
                problems.Add($"AppActive:Monitoring:Initial:{name} — допустимо {min}..{max}, получено {value}");
            }
        }

        Range(nameof(PollIntervalSeconds), PollIntervalSeconds, 1, 3600);
        Range(nameof(ActivityIdleSeconds), ActivityIdleSeconds, 1, 86400);
        Range(nameof(FlushSeconds), FlushSeconds, 1, 600);
        Range(nameof(RetentionDays), RetentionDays, 1, 3650);
        Range(nameof(AgentHeartbeatSeconds), AgentHeartbeatSeconds, 10, 3600);
        return problems;
    }
}

public sealed class WorkflowOptions
{
    /// <summary>Разрешить действия типа "script" (запуск процессов). По умолчанию выключено.</summary>
    public bool AllowScriptActions { get; set; }

    /// <summary>Период работы движка правил, сек.</summary>
    public int EvaluationSeconds { get; set; } = 60;

    public int WebhookTimeoutSeconds { get; set; } = 10;
}

public sealed class LoggingOptions
{
    public string Level { get; set; } = "";

    public int FileRetentionDays { get; set; } = 14;

    public bool WriteEventLog { get; set; } = true;
}
