using System.Collections.Concurrent;
using AppActive.Core.Models;
using AppActive.Core.Options;
using AppActive.Core.Services;

namespace AppActive.Service.Monitoring;

internal sealed record AgentReportSnapshot(
    int SessionId,
    string UserName,
    string Domain,
    int Pid,
    string AgentVersion,
    double CpuPercent,
    double WorkingSetMb,
    IReadOnlyList<string> Errors);

/// <summary>Состояние агентов в памяти: кто жив, когда отчитался, сколько потребляет.</summary>
internal sealed class AgentRegistry : IAgentStatusProvider
{
    private const int MaxErrors = 10;

    private readonly ConcurrentDictionary<int, AgentState> _agents = new();
    private readonly AppActiveOptions _options;

    public AgentRegistry(AppActiveOptions options) => _options = options;

    public sealed class AgentState
    {
        public int SessionId { get; init; }
        public string UserName { get; set; } = "";
        public string Domain { get; set; } = "";
        public int ProcessId { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? LastReportUtc { get; set; }
        public long ReportsTotal { get; set; }
        public double CpuPercent { get; set; }
        public double WorkingSetMb { get; set; }
        public string AgentVersion { get; set; } = "";
        public string LaunchMethod { get; set; } = "";
        public string? LastError { get; set; }
        public Queue<string> Errors { get; } = new();
    }

    public void RegisterSpawned(int sessionId, int processId, string userName, string domain, string launchMethod)
    {
        var state = _agents.GetOrAdd(sessionId, id => new AgentState { SessionId = id });
        state.ProcessId = processId;
        state.UserName = userName;
        state.Domain = domain;
        state.StartedUtc = DateTime.UtcNow;
        state.LaunchMethod = launchMethod;
        state.LastError = null;
    }

    public void RegisterFailure(int sessionId, string userName, string domain, string error)
    {
        var state = _agents.GetOrAdd(sessionId, id => new AgentState { SessionId = id });
        state.UserName = userName;
        state.Domain = domain;
        state.ProcessId = 0;
        state.LastError = error;
    }

    public void RecordReport(AgentReportSnapshot report)
    {
        var state = _agents.GetOrAdd(report.SessionId, id => new AgentState { SessionId = id });
        state.UserName = report.UserName;
        state.Domain = report.Domain;
        state.AgentVersion = report.AgentVersion;
        state.CpuPercent = report.CpuPercent;
        state.WorkingSetMb = report.WorkingSetMb;
        state.LastReportUtc = DateTime.UtcNow;
        state.ReportsTotal++;
        state.StartedUtc ??= DateTime.UtcNow;
        if (report.Pid > 0) state.ProcessId = report.Pid;
        state.LastError = null;

        foreach (var error in report.Errors)
        {
            if (string.IsNullOrWhiteSpace(error)) continue;
            lock (state.Errors)
            {
                state.Errors.Enqueue(error);
                while (state.Errors.Count > MaxErrors) state.Errors.Dequeue();
            }
            state.LastError = error;
        }
    }

    public bool TryGet(int sessionId, out AgentState state) => _agents.TryGetValue(sessionId, out state!);

    public bool IsAlive(int sessionId, TimeSpan stale)
    {
        if (!_agents.TryGetValue(sessionId, out var state)) return false;
        if (!state.LastReportUtc.HasValue) return false;
        return DateTime.UtcNow - state.LastReportUtc.Value <= stale;
    }

    public void Remove(int sessionId) => _agents.TryRemove(sessionId, out _);

    public List<AgentState> Snapshot() => _agents.Values.ToList();

    public DateTime? LastReportUtc => _agents.Values
        .Where(a => a.LastReportUtc.HasValue)
        .Select(a => a.LastReportUtc)
        .Max();

    public IReadOnlyList<AgentStatusDto> GetAgentStatuses(bool includeInactive)
    {
        var stale = TimeSpan.FromSeconds(Math.Clamp(_options.Monitoring.AgentStaleSeconds, 30, 3600));
        return _agents.Values
            .OrderBy(a => a.SessionId)
            .Where(a => includeInactive || IsAlive(a.SessionId, stale))
            .Select(a => new AgentStatusDto
            {
                SessionId = a.SessionId,
                UserName = a.UserName,
                Domain = a.Domain,
                Pid = a.ProcessId,
                StartedUtc = a.StartedUtc.HasValue ? Core.TimeUtil.ToIso(a.StartedUtc.Value) : null,
                LastReportUtc = a.LastReportUtc.HasValue ? Core.TimeUtil.ToIso(a.LastReportUtc.Value) : null,
                Alive = IsAlive(a.SessionId, stale),
                ReportsTotal = a.ReportsTotal,
                CpuPercent = Math.Round(a.CpuPercent, 3),
                WorkingSetMb = Math.Round(a.WorkingSetMb, 1),
                AgentVersion = a.AgentVersion,
                Errors = SnapshotErrors(a)
            })
            .ToList();
    }

    private static List<string> SnapshotErrors(AgentState state)
    {
        lock (state.Errors)
        {
            return state.Errors.ToList();
        }
    }
}

/// <summary>Счётчики приёма данных от агентов.</summary>
internal sealed class IngestCounters
{
    private long _reports;
    private long _samples;
    private long _seconds;
    private long _errors;
    private long _rejected;

    public DateTime? LastReportUtc { get; private set; }

    public string? LastReportUtcIso => LastReportUtc.HasValue ? Core.TimeUtil.ToIso(LastReportUtc.Value) : null;

    public void AddReport(int samples, double seconds, bool rejected, int errors)
    {
        Interlocked.Increment(ref _reports);
        Interlocked.Add(ref _samples, samples);
        Interlocked.Add(ref _errors, errors);
        if (rejected) Interlocked.Increment(ref _rejected);
        if (seconds > 0) Interlocked.Add(ref _seconds, (long)Math.Round(seconds));
        LastReportUtc = DateTime.UtcNow;
    }

    public object Snapshot() => new
    {
        reportsTotal = Interlocked.Read(ref _reports),
        samplesTotal = Interlocked.Read(ref _samples),
        secondsTotal = Interlocked.Read(ref _seconds),
        errorsTotal = Interlocked.Read(ref _errors),
        rejectedTotal = Interlocked.Read(ref _rejected),
        lastReportUtc = LastReportUtc.HasValue ? Core.TimeUtil.ToIso(LastReportUtc.Value) : null
    };
}
