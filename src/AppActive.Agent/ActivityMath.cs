namespace AppActive.Agent;

/// <summary>
/// Правило учёта активности в чистом виде — вынесено отдельно, чтобы его можно было
/// проверить детерминированно (см. AppActive.Agent.exe --self-test).
///
/// Приложение считается активным, если в течение N секунд была активность клавиатуры или мыши.
/// Засчитывается промежуток от начала отрезка (окно было активным) до момента последнего ввода:
/// при непрерывной работе это почти весь такт опроса, при разовом нажатии в начале такта — почти ноль,
/// а при простое дольше N секунд — ноль.
/// </summary>
internal static class ActivityMath
{
    public const double MinimumSampleSeconds = 0.5;

    public static double ComputeCreditSeconds(
        DateTime segmentStartUtc,
        DateTime segmentEndUtc,
        DateTime tickEndUtc,
        double idleMilliseconds,
        int activityIdleSeconds,
        double tickDeltaSeconds)
    {
        if (idleMilliseconds > activityIdleSeconds * 1000.0) return 0;
        if (segmentEndUtc <= segmentStartUtc) return 0;

        var lastInputAt = tickEndUtc.AddMilliseconds(-Math.Max(0, idleMilliseconds));
        var activeEnd = segmentEndUtc < lastInputAt ? segmentEndUtc : lastInputAt;
        if (activeEnd <= segmentStartUtc) return 0;

        var seconds = (activeEnd - segmentStartUtc).TotalSeconds;
        if (seconds > tickDeltaSeconds) seconds = tickDeltaSeconds;
        if (seconds < MinimumSampleSeconds) return 0;
        return Math.Round(seconds, 2);
    }
}
