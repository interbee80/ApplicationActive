using System.Diagnostics;
using System.Text;
using AppActive.Core;

namespace AppActive.Agent;

/// <summary>Сведения об окне (ленивое разрешение имени процесса).</summary>
internal sealed record WindowInfo(IntPtr Handle, int ProcessId, string ExeName, string? ExePath)
{
    public string AppKey => Core.Data.ActivityStore.BuildAppKey(ExeName, ExePath);

    public static readonly WindowInfo None = new(IntPtr.Zero, 0, "", null);
}

internal sealed record WindowSegment(DateTime StartUtc, DateTime EndUtc, WindowInfo? Window)
{
    public double Seconds => (EndUtc - StartUtc).TotalSeconds;
}

/// <summary>
/// Следит за активным окном: событийный крючок EVENT_SYSTEM_FOREGROUND даёт точные моменты
/// переключений, а опрос раз в N секунд — снимок состояния. Вместе это позволяет точно
/// распределить активное время по приложениям даже при большом интервале опроса.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly List<(DateTime From, WindowInfo Window)> _timeline = new();

    private readonly Dictionary<IntPtr, (string Title, DateTime At)> _titleCache = new();
    private readonly HashSet<int> _unresolvedPids = new();

    private NativeMethods.WinEventProc? _hookProc; // держим ссылку, иначе GC соберёт делегат
    private IntPtr _hook = IntPtr.Zero;
    private bool _pollOnly;

    public bool HookInstalled { get; private set; }

    public void Start()
    {
        _hookProc = OnWinEvent;
        try
        {
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _hookProc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            HookInstalled = _hook != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            HookInstalled = false;
            AgentLog.Warn($"Не удалось установить крючок активного окна: {ex.Message}");
        }

        _pollOnly = !HookInstalled;
        if (!HookInstalled)
        {
            AgentLog.Warn("Крючок EVENT_SYSTEM_FOREGROUND недоступен — работаем в режиме периодического опроса");
        }

        var now = DateTime.UtcNow;
        lock (_gate)
        {
            _timeline.Clear();
            _timeline.Add((now, Capture(NativeMethods.GetForegroundWindow())));
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            var info = Capture(hwnd);
            var stamp = DateTime.UtcNow;
            lock (_gate)
            {
                var last = _timeline.Count > 0 ? _timeline[^1] : (From: stamp, Window: WindowInfo.None);
                if (last.Window.Handle == info.Handle) return;
                _timeline.Add((stamp, info));
                if (_timeline.Count > 256) _timeline.RemoveRange(0, _timeline.Count - 128);
            }
        }
        catch
        {
            // Колбэк крючка не должен бросать исключения.
        }
    }

    /// <summary>
    /// Возвращает отрезки [from, to] с указанием окна, которое было активным в каждый момент.
    /// </summary>
    public List<WindowSegment> CollectSegments(DateTime from, DateTime to)
    {
        var result = new List<WindowSegment>();
        lock (_gate)
        {
            if (_pollOnly)
            {
                _timeline.Clear();
                _timeline.Add((from, Capture(NativeMethods.GetForegroundWindow())));
            }

            if (_timeline.Count == 0)
            {
                _timeline.Add((from, Capture(NativeMethods.GetForegroundWindow())));
            }

            // опорное состояние на момент "from"
            var baseWindow = _timeline[0].Window;
            foreach (var entry in _timeline)
            {
                if (entry.From <= from) baseWindow = entry.Window;
                else break;
            }

            var changes = _timeline.Where(x => x.From > from && x.From < to).OrderBy(x => x.From).ToList();
            var cursor = from;
            var current = baseWindow;
            foreach (var change in changes)
            {
                if (change.From > cursor) result.Add(new WindowSegment(cursor, change.From, current));
                cursor = change.From;
                current = change.Window;
            }
            if (to > cursor) result.Add(new WindowSegment(cursor, to, current));

            // чистим историю: оставляем только последнее состояние до "from" и всё после
            var keep = _timeline.Where(x => x.From >= from).ToList();
            keep.Insert(0, (from, baseWindow));
            _timeline.Clear();
            _timeline.AddRange(keep);
        }

        return result;
    }

    /// <summary>Заголовок окна (необязательно). Кэшируется и запрашивается с таймаутом.</summary>
    public string GetTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        lock (_gate)
        {
            if (_titleCache.TryGetValue(hwnd, out var cached) && (DateTime.UtcNow - cached.At).TotalSeconds < 30)
            {
                return cached.Title;
            }
        }

        var title = QueryTitle(hwnd);
        lock (_gate)
        {
            if (_titleCache.Count > 512) _titleCache.Clear();
            _titleCache[hwnd] = (title, DateTime.UtcNow);
        }
        return title;
    }

    private static string QueryTitle(IntPtr hwnd)
    {
        try
        {
            var buffer = new StringBuilder(512);
            var ok = NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETTEXT, (IntPtr)buffer.Capacity, buffer,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL, 100, out _);
            if (ok == IntPtr.Zero) return "";
            var title = buffer.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
            return title.Length > 200 ? title[..200] : title;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Определение процесса активного окна. Для недоступных процессов имя берётся из снимка процессов.</summary>
    public WindowInfo Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return WindowInfo.None;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return WindowInfo.None;

            var path = QueryProcessPath(pid);
            var exeName = string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path);
            if (string.IsNullOrEmpty(exeName))
            {
                exeName = QueryProcessName(pid);
                if (exeName.Length == 0)
                {
                    lock (_gate)
                    {
                        if (_unresolvedPids.Add((int)pid))
                        {
                            AgentLog.Warn($"Не удалось определить имя процесса PID={pid} (возможно, процесс запущен с повышенными правами)");
                        }
                    }
                    exeName = $"pid-{pid}.exe";
                }
            }

            return new WindowInfo(hwnd, (int)pid, exeName, path);
        }
        catch
        {
            return WindowInfo.None;
        }
    }

    private static string QueryProcessPath(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : "";
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string QueryProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (string.IsNullOrWhiteSpace(name)) return "";
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Простой: миллисекунды с последнего ввода с клавиатуры/мыши в этом сеансе.</summary>
    public static uint GetIdleMilliseconds()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return uint.MaxValue;
        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return elapsed;
    }

    /// <summary>Признак заблокированного сеанса или экрана входа.</summary>
    public static bool IsLockScreen(string exeName) =>
        exeName.Equals("LogonUI.exe", StringComparison.OrdinalIgnoreCase) ||
        exeName.Equals("LockApp.exe", StringComparison.OrdinalIgnoreCase) ||
        exeName.Equals("WindowsShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            try { NativeMethods.UnhookWinEvent(_hook); } catch { /* уже снят */ }
            _hook = IntPtr.Zero;
        }
    }
}
