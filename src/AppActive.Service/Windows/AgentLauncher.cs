using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AppActive.Core;

namespace AppActive.Service.Windows;

/// <summary>Включение привилегий текущего процесса (для SYSTEM нужны SeTcb/SeAssignPrimaryToken).</summary>
internal static class Privileges
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Включает перечисленные привилегии. Возвращает список успешно включённых.</summary>
    public static List<string> Enable(params string[] names)
    {
        var enabled = new List<string>();
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            AppLog.Warn($"Не удалось открыть токен процесса: {Marshal.GetLastWin32Error()}");
            return enabled;
        }

        try
        {
            foreach (var name in names)
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) continue;
                var privileges = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                };
                if (AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) &&
                    Marshal.GetLastWin32Error() == 0)
                {
                    enabled.Add(name);
                }
            }
        }
        finally
        {
            CloseHandle(token);
        }

        return enabled;
    }
}

/// <summary>Запуск агента в пользовательском сеансе от имени этого пользователя.</summary>
internal static class AgentLauncher
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint TokenAllAccess = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? applicationName,
        string commandLine, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public sealed class LaunchResult
    {
        public bool Success { get; init; }
        public int ProcessId { get; init; }
        public string Method { get; init; } = "";
        public string Error { get; init; } = "";
    }

    private static bool _privilegesPrepared;

    public static void PreparePrivileges()
    {
        if (_privilegesPrepared) return;
        _privilegesPrepared = true;
        var enabled = Privileges.Enable("SeTcbPrivilege", "SeAssignPrimaryTokenPrivilege", "SeIncreaseQuotaPrivilege", "SeImpersonatePrivilege");
        AppLog.Info(enabled.Count > 0
            ? $"Включены привилегии процесса: {string.Join(", ", enabled)}"
            : "Привилегии SeTcb/SeAssignPrimaryToken недоступны (запуск не от SYSTEM)");
    }

    public static LaunchResult Launch(AgentLaunchRequest request)
    {
        var commandLine = $"\"{request.ExecutablePath}\" --config \"{request.ConfigPath}\" --session {request.SessionId}";
        var workingDirectory = Path.GetDirectoryName(request.ExecutablePath) ?? request.ExecutablePath;

        var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };

        PreparePrivileges();

        // Основной путь: SYSTEM + токен пользователя сеанса (CreateProcessAsUser).
        var token = WtsInterop.QueryUserToken(request.SessionId, out var tokenError);
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        if (token != IntPtr.Zero)
        {
            try
            {
                if (DuplicateTokenEx(token, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
                {
                    var hasEnvironment = CreateEnvironmentBlock(out environment, primaryToken, false);
                    var flags = CreateNoWindow | (hasEnvironment ? CreateUnicodeEnvironment : 0);

                    if (CreateProcessAsUser(primaryToken, request.ExecutablePath, commandLine, IntPtr.Zero, IntPtr.Zero,
                            false, flags, environment, workingDirectory, ref startupInfo, out var processInfo))
                    {
                        CloseHandle(processInfo.hProcess);
                        CloseHandle(processInfo.hThread);
                        return new LaunchResult { Success = true, ProcessId = processInfo.dwProcessId, Method = "CreateProcessAsUser" };
                    }

                    var createError = Marshal.GetLastWin32Error();
                    if (CreateProcessWithTokenW(primaryToken, 0, request.ExecutablePath, commandLine, flags, environment,
                            workingDirectory, ref startupInfo, out var fallbackInfo))
                    {
                        CloseHandle(fallbackInfo.hProcess);
                        CloseHandle(fallbackInfo.hThread);
                        return new LaunchResult { Success = true, ProcessId = fallbackInfo.dwProcessId, Method = "CreateProcessWithTokenW" };
                    }

                    AppLog.Warn($"Не удалось создать процесс в сеансе {request.SessionId}: CreateProcessAsUser={createError}, " +
                                $"CreateProcessWithTokenW={Marshal.GetLastWin32Error()}");
                    tokenError = createError;
                }
                else
                {
                    tokenError = Marshal.GetLastWin32Error();
                    AppLog.Warn($"DuplicateTokenEx не удался: {tokenError}");
                }
            }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
                CloseHandle(token);
            }
        }
        else
        {
            AppLog.Warn($"WTSQueryUserToken для сеанса {request.SessionId} не удался: {new Win32Exception(tokenError).Message}");
        }

        // Резервный путь для отладочного запуска: свой сеанс, свой токен.
        if (request.AllowSelfSessionFallback && request.SessionId == Process.GetCurrentProcess().SessionId)
        {
            try
            {
                var procesStart = new ProcessStartInfo
                {
                    FileName = request.ExecutablePath,
                    Arguments = $"--config \"{request.ConfigPath}\" --session {request.SessionId}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                };
                var process = Process.Start(procesStart);
                if (process is not null)
                {
                    return new LaunchResult
                    {
                        Success = true,
                        ProcessId = process.Id,
                        Method = "Process.Start (режим отладки, без токена сеанса)"
                    };
                }
            }
            catch (Exception ex)
            {
                return new LaunchResult { Success = false, Error = ex.Message };
            }
        }

        return new LaunchResult
        {
            Success = false,
            Error = new Win32Exception(tokenError).Message
        };
    }

    /// <summary>Записывает конфигурацию агента в каталог agents (доступен пользователям на чтение).</summary>
    public static string WriteAgentConfig(string directory, int sessionId, string serviceUrl, string ingestKey, int pollSeconds,
        int idleSeconds, bool collectTitles, bool monitorEnabled, int heartbeatSeconds)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"session-{sessionId}.json");
        var payload = new
        {
            serviceUrl,
            apiKey = ingestKey,
            sessionId,
            pollIntervalSeconds = pollSeconds,
            activityIdleSeconds = idleSeconds,
            collectWindowTitles = collectTitles,
            monitorEnabled,
            heartbeatSeconds,
            logLevel = "info"
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}

internal sealed class AgentLaunchRequest
{
    public int SessionId { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string ConfigPath { get; init; } = "";
    public bool AllowSelfSessionFallback { get; init; }
}
