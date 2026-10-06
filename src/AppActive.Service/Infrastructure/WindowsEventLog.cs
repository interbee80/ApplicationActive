using System.Runtime.InteropServices;
using AppActive.Core;
using Microsoft.Win32;

namespace AppActive.Service.Infrastructure;

/// <summary>Запись в журнал событий Windows (не критично: ошибки глушатся).</summary>
internal static class WindowsEventLog
{
    private const ushort EventLogErrorType = 0x0001;
    private const ushort EventLogWarningType = 0x0002;
    private const ushort EventLogInformationType = 0x0004;

    private static bool _sourceReady;
    private static bool _disabled;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterEventSource(string? lpUNCServerName, string lpSourceName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReportEvent(IntPtr hEventLog, ushort wType, ushort wCategory, uint dwEventID,
        IntPtr lpUserSid, ushort wNumStrings, ushort dwDataSize, string[] lpStrings, IntPtr lpRawData);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeregisterEventSource(IntPtr hEventLog);

    /// <summary>Регистрирует источник в журнале приложений (нужны права администратора/SYSTEM).</summary>
    public static void TryRegisterSource(string source, string executablePath)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                $@"SYSTEM\CurrentControlSet\Services\EventLog\Application\{source}");
            if (key is null)
            {
                _disabled = true;
                return;
            }
            key.SetValue("EventMessageFile", executablePath, RegistryValueKind.ExpandString);
            key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
            _sourceReady = true;
        }
        catch (Exception ex)
        {
            _disabled = true;
            AppLog.Warn($"Журнал событий Windows недоступен: {ex.Message}");
        }
    }

    public static void Info(string source, string message) => Write(source, EventLogInformationType, message);

    public static void Warning(string source, string message) => Write(source, EventLogWarningType, message);

    public static void Error(string source, string message) => Write(source, EventLogErrorType, message);

    private static void Write(string source, ushort type, string message)
    {
        if (_disabled || !_sourceReady) return;
        try
        {
            var handle = RegisterEventSource(null, source);
            if (handle == IntPtr.Zero)
            {
                _disabled = true;
                return;
            }
            try
            {
                var lines = new[] { message.Length > 30000 ? message[..30000] : message };
                ReportEvent(handle, type, 0, 1000, IntPtr.Zero, 1, 0, lines, IntPtr.Zero);
            }
            finally
            {
                DeregisterEventSource(handle);
            }
        }
        catch
        {
            _disabled = true;
        }
    }
}
