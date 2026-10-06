using AppActive.Core.Data;
using AppActive.Core.Options;
using AppActive.Core.Services;
using AppActive.Service.Monitoring;
using AppActive.Service.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace AppActive.Service.Infrastructure;

/// <summary>Единая регистрация служб приложения (используется и Web-хостом, и режимами CLI).</summary>
internal static class AppServices
{
    public static void Register(IServiceCollection services, AppActiveOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<ServiceEndpoint>();
        services.AddSingleton(_ => new SqliteDatabase(options.DatabasePath));
        services.AddSingleton<ActivityStore>();
        services.AddSingleton<RulesStore>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(sp => new SettingsService(
            sp.GetRequiredService<SettingsStore>(),
            options.Monitoring.Initial));
        services.AddSingleton(sp => new ActivityAccumulator(
            sp.GetRequiredService<ActivityStore>(),
            () => sp.GetRequiredService<SettingsService>().Current));
        services.AddSingleton<CriticalEvaluator>();
        services.AddSingleton<WorkflowEngine>();
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<IngestCounters>();
        services.AddSingleton<ServiceMetrics>();
        services.AddSingleton<ProcessManager>();
        services.AddSingleton<IAgentStatusProvider>(sp => sp.GetRequiredService<AgentRegistry>());
        services.AddSingleton(sp => new DashboardQueryService(
            sp.GetRequiredService<ActivityStore>(),
            sp.GetRequiredService<RulesStore>(),
            sp.GetRequiredService<CriticalEvaluator>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<AgentRegistry>()));
    }
}
