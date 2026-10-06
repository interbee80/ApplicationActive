using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AppActive.Agent;

/// <summary>Отправка отчётов службе. Неудачные попытки складываются в буфер и уходят следующим отчётом.</summary>
internal sealed class Reporter : IDisposable
{
    private const int MaxBufferedSamples = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly AgentConfig _config;
    private readonly HttpClient _http;
    private readonly Queue<AgentSampleDto> _buffer = new();
    private readonly object _gate = new();

    public Reporter(AgentConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public long ReportsTotal { get; private set; }
    public long SamplesTotal { get; private set; }
    public long FailuresTotal { get; private set; }

    /// <summary>Подряд неудачных отправок. Сбрасывается при первом успешном отчёте.</summary>
    public int ConsecutiveFailures { get; private set; }

    public string? LastError { get; private set; }
    public DateTime? LastSuccessUtc { get; private set; }
    public int BufferedSamples
    {
        get { lock (_gate) return _buffer.Count; }
    }

    public bool Send(IReadOnlyList<AgentSampleDto> samples, double cpuPercent, double workingSetMb, bool includeErrors)
    {
        var payload = new AgentReportRequest
        {
            SessionId = _config.SessionId,
            UserName = _config.UserName,
            Domain = _config.Domain,
            Pid = Environment.ProcessId,
            AgentVersion = AgentVersion.Value,
            CpuPercent = Math.Round(cpuPercent, 3),
            WorkingSetMb = Math.Round(workingSetMb, 1),
            Samples = new List<AgentSampleDto>(samples)
        };

        lock (_gate)
        {
            foreach (var buffered in _buffer) payload.Samples.Add(buffered);
            _buffer.Clear();
        }

        if (includeErrors && LastError is not null) payload.Errors.Add(LastError);

        var url = _config.ServiceUrl.TrimEnd('/') + "/api/ingest/report";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("X-AppActive-Key", _config.ApiKey);
            }

            using var response = _http.SendAsync(request).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)response.StatusCode}: {Truncate(body)}";
                FailuresTotal++;
                ConsecutiveFailures++;
                Requeue(payload.Samples);
                return false;
            }

            var parsed = JsonSerializer.Deserialize<AgentReportResponse>(body, JsonOptions);
            if (parsed?.Config is not null) _config.ApplyFrom(parsed.Config);

            ReportsTotal++;
            SamplesTotal += payload.Samples.Count;
            ConsecutiveFailures = 0;
            LastSuccessUtc = DateTime.UtcNow;
            if (LastError is not null)
            {
                AgentLog.Info($"Связь со службой восстановлена (предыдущая ошибка: {LastError})");
                LastError = null;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            FailuresTotal++;
            ConsecutiveFailures++;
            Requeue(payload.Samples);
            return false;
        }
    }

    private void Requeue(List<AgentSampleDto> samples)
    {
        if (samples.Count == 0) return;
        lock (_gate)
        {
            foreach (var sample in samples) _buffer.Enqueue(sample);
            while (_buffer.Count > MaxBufferedSamples) _buffer.Dequeue();
        }
    }

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Length > 200 ? value[..200] : value;

    public void Dispose() => _http.Dispose();
}

internal static class AgentVersion
{
    public static readonly string Value =
        typeof(AgentVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}

internal static class AgentResource
{
    public static double WorkingSetMb
    {
        get
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.WorkingSet64 / 1024.0 / 1024.0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
