using System.Runtime.InteropServices;

namespace AppActive.Service.Windows;

/// <summary>P/Invoke для диспетчера служб Windows (SCM).</summary>
internal static class ServiceInterop
{
    public const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    public const uint SERVICE_ACCEPT_STOP = 0x00000001;
    public const uint SERVICE_ACCEPT_SHUTDOWN = 0x00000004;

    public const uint SERVICE_CONTROL_STOP = 0x00000001;
    public const uint SERVICE_CONTROL_INTERROGATE = 0x00000004;
    public const uint SERVICE_CONTROL_SHUTDOWN = 0x00000005;

    public const uint SERVICE_STOPPED = 0x00000001;
    public const uint SERVICE_START_PENDING = 0x00000002;
    public const uint SERVICE_STOP_PENDING = 0x00000003;
    public const uint SERVICE_RUNNING = 0x00000004;

    public const int ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void ServiceMainCallback(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint ServiceControlHandlerEx(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_TABLE_ENTRY
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpServiceName;
        public ServiceMainCallback? lpServiceProc;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool StartServiceCtrlDispatcher([In] SERVICE_TABLE_ENTRY[] lpServiceStartTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr RegisterServiceCtrlHandlerEx(string lpServiceName, ServiceControlHandlerEx lpHandlerProc, IntPtr lpContext);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetServiceStatus(IntPtr hServiceStatus, ref SERVICE_STATUS lpServiceStatus);
}
