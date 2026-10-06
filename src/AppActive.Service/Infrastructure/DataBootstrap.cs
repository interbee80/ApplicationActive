using System.Diagnostics;
using System.Security.Cryptography;
using AppActive.Core;
using AppActive.Core.Options;

namespace AppActive.Service.Infrastructure;

/// <summary>Подготовка каталога данных: права доступа и ключ агентов.</summary>
internal static class DataBootstrap
{
    public const string IngestKeyFileName = "agent.key";

    public static string Prepare(AppActiveOptions options, bool hardenAcl = true)
    {
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.LogsDirectory);
        Directory.CreateDirectory(options.AgentDirectory);

        if (hardenAcl) Harden(options);
        return EnsureIngestKey(options);
    }

    /// <summary>
    /// Ключ агентов: из конфигурации, из файла или сгенерированный. Файл лежит в каталоге данных,
    /// доступном только SYSTEM и администраторам; копия передаётся агентам в их конфигурации.
    /// </summary>
    public static string EnsureIngestKey(AppActiveOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Security.IngestKey))
        {
            return options.Security.IngestKey.Trim();
        }

        var path = Path.Combine(options.DataDirectory, IngestKeyFileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 16)
                {
                    options.Security.IngestKey = existing;
                    return existing;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось прочитать ключ агентов '{path}': {ex.Message}");
        }

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "").Replace("/", "").Replace("=", "");
        try
        {
            File.WriteAllText(path, generated);
            AppLog.Info($"Сгенерирован новый ключ агентов: {path}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось сохранить ключ агентов: {ex.Message}");
        }

        options.Security.IngestKey = generated;
        return generated;
    }

    /// <summary>
    /// Ограничивает доступ к каталогу данных: SYSTEM и администраторы — полный,
    /// пользователи — чтение/запись только в подкаталоге agents (там лежат конфигурации агентов).
    /// </summary>
    private static void Harden(AppActiveOptions options)
    {
        // SID: SYSTEM, Administrators, Users
        RunIcacls(options.DataDirectory, "/inheritance:r",
            "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F");
        RunIcacls(options.AgentDirectory, "/inheritance:r",
            "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-32-545:(OI)(CI)M");
    }

    private static void RunIcacls(string path, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(path);
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null) return;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            if (process.ExitCode != 0)
            {
                AppLog.Warn($"icacls завершился с кодом {process.ExitCode} для '{path}'");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось настроить права на '{path}': {ex.Message}");
        }
    }
}
