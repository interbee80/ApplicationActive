using System.Runtime.InteropServices;

namespace AppActive.Service.Windows;

internal sealed class WtsSession
{
    public int SessionId { get; init; }
    public string UserName { get; init; } = "";
    public string Domain { get; init; } = "";
    public string WinStationName { get; init; } = "";
    public int State { get; init; }
    public string StateName { get; init; } = "";

    public string DisplayUser => string.IsNullOrEmpty(Domain) ? UserName : $"{Domain}\\{UserName}";
}

/// <summary>Список сеансов Windows и токены пользователей (WTS API).</summary>
internal static class WtsInterop
{
    private const int WtsCurrentServerHandle = 0;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private const int WtsWinStationName = 6;

    // WTS_CONNECTSTATE_CLASS
    public const int StateActive = 0;
    public const int StateConnected = 1;
    public const int StateConnectQuery = 2;
    public const int StateShadow = 3;
    public const int StateDisconnected = 4;
    public const int StateIdle = 5;
    public const int StateListen = 6;
    public const int StateReset = 7;
    public const int StateDown = 8;
    public const int StateInit = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionID;
        public IntPtr pWinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WTSQuerySessionInformation(IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("wtsapi32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    public static uint ActiveConsoleSessionId() => WTSGetActiveConsoleSessionId();

    public static List<WtsSession> Enumerate()
    {
        var result = new List<WtsSession>();
        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count) == 0)
        {
            return result;
        }

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                var userName = QueryString(info.SessionID, WtsUserName);
                var domain = QueryString(info.SessionID, WtsDomainName);
                var winStation = info.pWinStationName == IntPtr.Zero
                    ? ""
                    : Marshal.PtrToStringUni(info.pWinStationName) ?? "";

                result.Add(new WtsSession
                {
                    SessionId = info.SessionID,
                    UserName = userName,
                    Domain = domain,
                    WinStationName = winStation,
                    State = info.State,
                    StateName = StateNameOf(info.State)
                });
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return result;
    }

    public static bool IsInteractive(int state) => state is StateActive or StateConnected;

    public static string StateNameOf(int state) => state switch
    {
        StateActive => "Active",
        StateConnected => "Connected",
        StateConnectQuery => "ConnectQuery",
        StateShadow => "Shadow",
        StateDisconnected => "Disconnected",
        StateIdle => "Idle",
        StateListen => "Listen",
        StateReset => "Reset",
        StateDown => "Down",
        StateInit => "Init",
        _ => $"Unknown({state})"
    };

    private static string QueryString(int sessionId, int infoClass)
    {
        if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytes) == 0)
        {
            return "";
        }

        try
        {
            return bytes > 2 ? Marshal.PtrToStringUni(buffer) ?? "" : "";
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Токен пользователя сеанса. Требует прав SYSTEM (SeTcbPrivilege).</summary>
    public static IntPtr QueryUserToken(int sessionId, out int error)
    {
        if (WTSQueryUserToken((uint)sessionId, out var token))
        {
            error = 0;
            return token;
        }

        error = Marshal.GetLastWin32Error();
        return IntPtr.Zero;
    }

    public static string GetWinStationName(int sessionId) => QueryString(sessionId, WtsWinStationName);
}
