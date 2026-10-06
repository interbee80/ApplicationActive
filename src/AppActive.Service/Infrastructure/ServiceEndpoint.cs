using AppActive.Core.Options;

namespace AppActive.Service.Infrastructure;

/// <summary>
/// Фактический адрес, по которому служба принимает запросы.
/// Заполняется после старта Kestrel из IServerAddressesFeature; агенты сеансов обращаются
/// по loopback-адресу именно отсюда. Благодаря этому порт задаётся ровно в одном месте —
/// AppActive:Urls в appsettings.json — и нигде больше не дублируется.
/// </summary>
internal sealed class ServiceEndpoint
{
    private readonly AppActiveOptions _options;
    private volatile string? _loopbackUrl;

    public ServiceEndpoint(AppActiveOptions options) => _options = options;

    /// <summary>Адрес вида http://127.0.0.1:порт для локальных агентов.</summary>
    public string? LoopbackUrl => _loopbackUrl;

    public IReadOnlyList<string> BoundAddresses { get; private set; } = Array.Empty<string>();

    public void Capture(IEnumerable<string> addresses)
    {
        var list = addresses.Where(address => !string.IsNullOrWhiteSpace(address)).ToArray();
        if (list.Length == 0) return;

        BoundAddresses = list;
        foreach (var address in list)
        {
            var loopback = ToLoopback(address);
            if (loopback is not null)
            {
                _loopbackUrl = loopback;
                break;
            }
        }
    }

    /// <summary>
    /// Адрес, выведенный из конфигурации: нужен до старта хоста и как запасной вариант,
    /// если Kestrel ещё не сообщил реальные адреса.
    /// </summary>
    public string FromConfiguration() =>
        _options.Urls.Select(ToLoopback).FirstOrDefault(url => url is not null)
        ?? _options.Urls.FirstOrDefault()
        ?? "";

    private static string? ToLoopback(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        if (host is "0.0.0.0" or "::" or "[::]" or "*" or "+" or "localhost")
        {
            host = "127.0.0.1";
        }
        return $"{uri.Scheme}://{host}:{uri.Port}";
    }
}
