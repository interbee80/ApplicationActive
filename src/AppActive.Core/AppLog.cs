namespace AppActive.Core;

/// <summary>
/// Минимальный «мост» логирования: ядро не зависит от конкретного логгера,
/// служба подставляет свой sink (файл + консоль + журнал событий Windows).
/// </summary>
public static class AppLog
{
    public static Action<string, Exception?>? Sink { get; set; }

    public static void Info(string message) => Write(message, null);

    public static void Warn(string message, Exception? error = null) => Write(message, error);

    public static void Error(string message, Exception? error = null) => Write(message, error);

    private static void Write(string message, Exception? error)
    {
        try
        {
            Sink?.Invoke(message, error);
        }
        catch
        {
            // Логирование не должно ломать учёт.
        }
    }
}
