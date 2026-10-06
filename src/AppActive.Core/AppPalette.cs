namespace AppActive.Core;

/// <summary>
/// Палитра для окраски приложений на дашборде. Цвет выдаётся приложению службой
/// (поле appColors в /api/dashboard), поэтому одно и то же приложение одинаково
/// окрашено в графике, легенде, ленте дня, таблице и чипах групп.
/// </summary>
public static class AppPalette
{
    public static readonly string[] Colors =
    {
        "#4f8cff", "#3fb27f", "#e0a33a", "#d95757", "#8a6fe8", "#2fa8c9",
        "#c95fa8", "#6fae3f", "#e07b39", "#5b7cc9", "#b45fd9", "#3fb7a4",
        "#c9a23f", "#d95f7b", "#7f8ce0", "#4fb07f", "#c96f3f", "#9b8ce0",
        "#3f9ec9", "#d9a05f", "#5fb0d9", "#a8c93f", "#e05f5f", "#5f8ad9"
    };

    public const string Other = "#8b93a7";

    public static string ForIndex(int index)
    {
        var normalized = index % Colors.Length;
        if (normalized < 0) normalized += Colors.Length;
        return Colors[normalized];
    }

    /// <summary>Цвет по идентификатору приложения — используется как запасной вариант.</summary>
    public static string ForAppId(long appId) => ForIndex((int)(appId % Colors.Length));
}
