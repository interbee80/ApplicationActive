using AppActive.Core;
using AppActive.Core.Data;
using AppActive.Core.Options;
using AppActive.Service.Api;
using AppActive.Service.Infrastructure;
using AppActive.Service.Monitoring;
using AppActive.Service.Windows;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AppActive.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        var cli = CliOptions.Parse(args);
        if (cli.ShowHelp)
        {
            CliOptions.PrintHelp();
            return 0;
        }

        try
        {
            switch (cli.Mode)
            {
                case CliOptions.ModeSelfTest:
                    return SelfTest.Run();
                case CliOptions.ModeSeedDemo:
                    return RunSeedDemo(cli);
                default:
                    return RunHost(cli);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Критическая ошибка: " + ex);
            return 1;
        }
    }

    // ------------------------------------------------------------------ хост

    private static int RunHost(CliOptions cli)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = cli.Passthrough.ToArray(),
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });

        var options = BuildOptions(builder.Configuration, cli);

        // Конфигурация — единственный источник адресов, путей и имён: проверяем её до запуска хоста.
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            Console.Error.WriteLine("Конфигурация ApplicationActive заполнена не полностью:");
            foreach (var problem in problems) Console.Error.WriteLine("  - " + problem);
            Console.Error.WriteLine($"Проверьте файл: {Path.Combine(AppContext.BaseDirectory, "appsettings.json")}");
            return 1;
        }

        var ingestKey = DataBootstrap.Prepare(options);

        var level = ParseLevel(options.Logging.Level);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(level);
        builder.Logging.AddProvider(new FileLoggerProvider(new FileLoggerOptions
        {
            Directory = options.LogsDirectory,
            MinimumLevel = level,
            RetentionDays = options.Logging.FileRetentionDays,
            // Пишем и в консоль: у службы её нет (запись уходит в никуда), зато при ручном
            // запуске в режиме --service все сообщения видны на экране.
            WriteToConsole = true,
            EventLogSource = options.Logging.WriteEventLog ? options.ServiceName : null
        }));

        builder.WebHost.UseUrls(options.Urls);

        AppServices.Register(builder.Services, options);

        // Фоновые службы регистрируем и как конкретные типы, чтобы они были доступны в /api/diagnostics.
        builder.Services.AddSingleton<MonitoringWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringWorker>());
        if (options.Monitoring.Enabled)
        {
            builder.Services.AddSingleton<AgentSupervisor>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentSupervisor>());
        }

        var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AppActive");
        AppLog.Sink = (message, error) =>
        {
            if (error is not null) logger.LogWarning("{Message} | {Error}", message, error.Message);
            else logger.LogInformation("{Message}", message);
        };
        if (options.Logging.WriteEventLog)
        {
            WindowsEventLog.TryRegisterSource(options.ServiceName, Environment.ProcessPath ?? "");
        }

        // Фактические адреса Kestrel узнаём после старта: агенты сеансов получают loopback-адрес отсюда,
        // поэтому порт нигде не дублируется и меняется только в appsettings.json.
        var endpoint = app.Services.GetRequiredService<ServiceEndpoint>();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses?.ToArray() ?? Array.Empty<string>();
            endpoint.Capture(addresses);

            if (addresses.Length == 0)
            {
                endpoint.Capture(options.Urls);
            }

            foreach (var address in addresses.DefaultIfEmpty("(адреса не сообщены)"))
            {
                AppLog.Info($"Web-панель доступна по адресу: {address}");
            }
            AppLog.Info($"Локальный адрес для агентов сеансов: {endpoint.LoopbackUrl ?? "(не определён)"}");
        });

        var database = app.Services.GetRequiredService<SqliteDatabase>();
        database.Initialize();

        app.UseDefaultFiles();
        app.UseStaticFiles();
        ApiEndpoints.Map(app, options);
        app.MapFallbackToFile("index.html");

        var urls = string.Join(", ", options.Urls);
        AppLog.Info($"ApplicationActive {ApiEndpoints.Version} запускается. Режим: {cli.Mode}. Адреса из appsettings.json: {urls}");
        AppLog.Info($"Каталог данных: {options.DataDirectory}; база: {database.Path}; журналы: {options.LogsDirectory}");
        AppLog.Info($"Мониторинг сеансов: {(options.Monitoring.Enabled ? "включён" : "выключен")}; запуск агентов: {options.Monitoring.LaunchAgents}");
        AppLog.Info($"Ключ агентов: {(string.IsNullOrEmpty(ingestKey) ? "не задан" : ingestKey[..Math.Min(8, ingestKey.Length)] + "…")}; " +
                    $"ключ администратора: {(string.IsNullOrWhiteSpace(options.Security.AdminKey) ? "не задан (изменения только с localhost)" : "задан")}");

        if (cli.Mode == CliOptions.ModeConsole)
        {
            AppLog.Info("Консольный режим: остановите процесс сочетанием Ctrl+C");
            RunWithBindDiagnostics(app, options);
            return 0;
        }

        if (cli.Mode == CliOptions.ModeService)
        {
            if (!NativeServiceHost.TryStartDispatcher(options.ServiceName, app, out var serviceExitCode))
            {
                AppLog.Error("Режим --service, но процесс запущен не диспетчером служб. " +
                             $"Запустите службу командой: sc.exe start {options.ServiceName} " +
                             "(или используйте --console для отладочного запуска)");
                return 1;
            }
            return serviceExitCode;
        }

        // Режим auto: если процесс запущен диспетчером служб — работаем как служба,
        // иначе (запуск из консоли) остаёмся в консольном режиме.
        if (NativeServiceHost.TryStartDispatcher(options.ServiceName, app, out var exitCode))
        {
            return exitCode;
        }

        AppLog.Info("Процесс запущен не диспетчером служб — продолжаем в консольном режиме (Ctrl+C для остановки)");
        RunWithBindDiagnostics(app, options);
        return 0;
    }

    /// <summary>
    /// Запускает хост и превращает ошибку «порт занят» в понятное сообщение
    /// с указанием, где этот порт задаётся.
    /// </summary>
    private static void RunWithBindDiagnostics(WebApplication app, AppActiveOptions options)
    {
        try
        {
            app.Run();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException ||
                                   ex.GetType().Name.Contains("AddressInUse", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Error($"Не удалось запустить Web-панель по адресу {string.Join(", ", options.Urls)}: {ex.Message}");
            AppLog.Error("Скорее всего порт уже занят другой программой или вторым экземпляром ApplicationActive. " +
                         "Измените AppActive:Urls в appsettings.json (это единственное место, где задаётся порт) " +
                         "и перезапустите службу.");
            throw;
        }
    }

    // ------------------------------------------------------------------ CLI

    private static int RunSeedDemo(CliOptions cli)
    {
        var services = BuildCliServices(cli, out var options);
        using (services)
        {
            services.GetRequiredService<SqliteDatabase>().Initialize();
            var written = DemoSeeder.Seed(services, cli.SeedDays);
            Console.WriteLine($"Демонстрационные данные добавлены в {options.DatabasePath}: строк активности {written}, период {cli.SeedDays} сут.");
        }
        return 0;
    }

    private static ServiceProvider BuildCliServices(CliOptions cli, out AppActiveOptions options)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var resolved = BuildOptions(configuration, cli);
        options = resolved;
        DataBootstrap.Prepare(resolved, hardenAcl: false);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(new FileLoggerOptions
            {
                Directory = resolved.LogsDirectory,
                MinimumLevel = LogLevel.Information,
                RetentionDays = resolved.Logging.FileRetentionDays,
                WriteToConsole = true
            }));
        });
        AppServices.Register(services, resolved);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Читает конфигурацию из appsettings.json (значения по умолчанию в коде отсутствуют)
    /// и применяет только явные переопределения из командной строки.
    /// </summary>
    private static AppActiveOptions BuildOptions(IConfiguration configuration, CliOptions cli)
    {
        var options = configuration.GetSection("AppActive").Get<AppActiveOptions>() ?? new AppActiveOptions();
        options.Normalize();

        if (!string.IsNullOrWhiteSpace(cli.DataDirectory))
        {
            options.DataDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cli.DataDirectory));
        }

        // Явные переопределения адреса — только для отладки и аварийных случаев;
        // штатно адрес и порт задаются в AppActive:Urls файла appsettings.json.
        if (!string.IsNullOrWhiteSpace(cli.Urls))
        {
            options.Urls = cli.Urls.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Console.WriteLine($"ВНИМАНИЕ: адрес Web-панели переопределён из командной строки: {cli.Urls}");
        }
        else if (cli.Port.HasValue)
        {
            options.Urls = new[] { $"http://127.0.0.1:{cli.Port.Value}" };
            Console.WriteLine($"ВНИМАНИЕ: порт Web-панели переопределён из командной строки: {cli.Port.Value}");
        }

        if (cli.NoMonitor)
        {
            options.Monitoring.Enabled = false;
            options.Monitoring.LaunchAgents = false;
        }

        return options;
    }

    private static LogLevel ParseLevel(string? value) => (value ?? "").ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information
    };
}
