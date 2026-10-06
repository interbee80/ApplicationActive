using System.Net;
using AppActive.Core.Options;

namespace AppActive.Service.Api;

/// <summary>Проверки доступа к API: чтение, изменение и ключ агентов.</summary>
internal static class Guard
{
    public const string KeyHeader = "X-AppActive-Key";

    public static bool IsLoopback(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null) return true;
        if (IPAddress.IsLoopback(remote)) return true;
        return remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4());
    }

    /// <summary>Чтение: разрешено всегда с loopback, с других машин — если AllowRemoteRead.</summary>
    public static IResult? Read(HttpContext context, AppActiveOptions options)
    {
        if (options.Security.AllowRemoteRead || IsLoopback(context)) return null;
        return Results.Json(new { error = "Чтение API с других машин запрещено (AppActive:Security:AllowRemoteRead=false)" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Изменение: если задан AdminKey — требуется он; иначе только с loopback.
    /// </summary>
    public static IResult? Write(HttpContext context, AppActiveOptions options)
    {
        var adminKey = options.Security.AdminKey?.Trim() ?? "";
        if (adminKey.Length > 0)
        {
            var provided = context.Request.Headers[KeyHeader].ToString();
            if (!FixedTimeEquals(provided, adminKey))
            {
                return Results.Json(new { error = "Требуется корректный ключ администратора в заголовке " + KeyHeader },
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            return null;
        }

        if (!IsLoopback(context))
        {
            return Results.Json(new { error = "Изменения разрешены только с локальной машины. Задайте AppActive:Security:AdminKey для удалённого доступа." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    /// <summary>Ключ агента обязателен всегда (ingest).</summary>
    public static bool CheckIngestKey(HttpContext context, AppActiveOptions options)
    {
        var expected = options.Security.IngestKey?.Trim() ?? "";
        if (expected.Length == 0) return false;
        var provided = context.Request.Headers[KeyHeader].ToString();
        return FixedTimeEquals(provided, expected);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length || a.Length == 0) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
