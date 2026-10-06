namespace AppActive.Agent;

/// <summary>
/// Детерминированная проверка правила учёта активности (AppActive.Agent.exe --self-test).
/// Проверяет, что засчитывается ровно то время, когда окно было активным и была
/// активность клавиатуры или мыши в пределах N секунд.
/// </summary>
internal static class AgentSelfTest
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        Console.WriteLine("AppActive.Agent — самопроверка правила учёта активности");
        var tickEnd = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tickStart = tickEnd.AddSeconds(-5); // интервал опроса 5 с
        const int n = 60;                        // окно активности N

        // 1. Непрерывная работа: последний ввод 0.1 с назад — засчитывается почти весь такт.
        var credit = ActivityMath.ComputeCreditSeconds(tickStart, tickEnd, tickEnd, 100, n, 5);
        Check($"непрерывная работа: {credit:0.0} с из 5 с", credit is > 4.5 and <= 5.0);

        // 2. Последний ввод ровно в начале такта — окно активно всё время, но ввод был только в начале:
        //    засчитывается время до момента ввода, то есть ноль.
        credit = ActivityMath.ComputeCreditSeconds(tickStart, tickEnd, tickEnd, 5000, n, 5);
        Check($"разовый ввод в начале такта: {credit:0.0} с", credit == 0);

        // 3. Ввод 1 секунду назад при такте 5 с — засчитывается 4 с (время до последнего ввода).
        credit = ActivityMath.ComputeCreditSeconds(tickStart, tickEnd, tickEnd, 1000, n, 5);
        Check($"ввод 1 с назад: {credit:0.0} с (ожидается ~4)", Math.Abs(credit - 4) < 0.01);

        // 4. Простой дольше N секунд — приложение не считается активным.
        credit = ActivityMath.ComputeCreditSeconds(tickStart, tickEnd, tickEnd, (n + 5) * 1000, n, 5);
        Check($"простой {n + 5} с при N={n} с: {credit:0.0} с", credit == 0);

        // 5. Простой внутри окна N, но больше такта — вводов в этом такте не было.
        credit = ActivityMath.ComputeCreditSeconds(tickStart, tickEnd, tickEnd, 12000, n, 5);
        Check("простой 12 с при такте 5 с: не засчитывается", credit == 0);

        // 6. Окно было активным только половину такта (переключение приложения).
        var switched = tickEnd.AddSeconds(-2.5);
        credit = ActivityMath.ComputeCreditSeconds(switched, tickEnd, tickEnd, 100, n, 5);
        Check($"окно активно 2.5 с при непрерывном вводе: {credit:0.0} с", credit is > 2.0 and <= 2.5);

        // 7. Окно только что получило фокус, но ввода после этого не было — время не засчитывается.
        credit = ActivityMath.ComputeCreditSeconds(tickEnd.AddSeconds(-1), tickEnd, tickEnd, 5000, n, 5);
        Check("окно получило фокус 1 с назад, ввода не было: 0 с", credit == 0);

        // 8. Слишком короткий отрезок (меньше порога 0.5 с) отбрасывается, чтобы не засорять базу.
        credit = ActivityMath.ComputeCreditSeconds(tickEnd.AddSeconds(-0.3), tickEnd, tickEnd, 50, n, 5);
        Check("отрезок короче 0.5 с отбрасывается", credit == 0);

        // 9. Кредит никогда не превышает длительность такта.
        credit = ActivityMath.ComputeCreditSeconds(tickStart, tickEnd.AddSeconds(3), tickEnd.AddSeconds(3), 10, n, 5);
        Check($"кредит ограничен тактом: {credit:0.0} с", credit <= 5.0);

        // 10. Большое N (пользователь смотрит видео и изредка двигает мышь) — ввод 30 с назад.
        credit = ActivityMath.ComputeCreditSeconds(tickStart, tickEnd, tickEnd, 30000, 300, 5);
        Check("при N=300 с простой 30 с не мешает, но вводов в такте не было", credit == 0);

        Console.WriteLine($"Итог: успешно {_passed}, ошибок {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string title, bool ok)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  [OK]   " + title);
        }
        else
        {
            _failed++;
            Console.WriteLine("  [FAIL] " + title);
        }
    }
}
