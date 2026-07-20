using System.Net.Http;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Builds the dependency-injection host, wiring Serilog and the application services.
/// </summary>
internal static class AppHost
{
    /// <summary>
    /// Builds a host; when agentTarget is set, adds the Windows-service-hosted agent.
    /// </summary>
    public static IHost Build(string? agentTarget)
    {
        Directory.CreateDirectory(TunnelPaths.LogDirectory());

        var builder = Host.CreateApplicationBuilder();

        // Structured log store (ageo + routes tables in logs\log.db), shared by the agent and per-tunnel
        // processes over WAL. Registered by factory so the container owns and flushes it on shutdown; AppEntry
        // initializes it and binds the static routing-log writer to it.
        builder.Services.AddSingleton(_ => new SqliteLogStore(TunnelPaths.LogDbFile()));

        // Live verbosity switch: shared by both processes, kept in sync with the "log-level" setting.
        var logLevel = new LogLevelController();
        builder.Services.AddSingleton(logLevel);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((services, config) =>
        {
            config.MinimumLevel.ControlledBy(logLevel.Switch)
                .Enrich.FromLogContext()
                // Source column: the logger's class name, derived from SourceContext.
                .Enrich.With(new LogSourceEnricher())
                .WriteTo.Console()
                .WriteTo.Sink(new LogDbSink(services.GetRequiredService<SqliteLogStore>()));
        });

        RegisterServices(builder.Services);

        if (agentTarget is not null)
        {
            // Retention cap lives in logs\settings.json; only the agent prunes (per-tunnel processes just insert).
            builder.Services.AddSingleton(LogSettings.LoadOrCreate(TunnelPaths.LogSettingsFile()));
            builder.Services.AddWindowsService(options => options.ServiceName = TunnelPaths.AgentServiceName());
            builder.Services.AddSingleton(new AgentTarget(agentTarget));
            builder.Services.AddHostedService<AgentBackgroundService>();
            builder.Services.AddHostedService<NetworkWatcher>();
            builder.Services.AddHostedService<StatusPipeServer>();
            builder.Services.AddHostedService<UpdateCheckService>();
            builder.Services.AddHostedService<GeoUpdateCheckService>();
            builder.Services.AddHostedService<GeoBootstrapService>();
            builder.Services.AddHostedService<LogLevelBackgroundWatcher>();
            builder.Services.AddHostedService<LogMaintenanceService>();
        }

        return builder.Build();
    }

    private static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IStateStore>(_ => new SqliteStateStore(TunnelPaths.StateDbFile()));
        services.AddSingleton<AgentControl>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<ServiceManager>();
        services.AddSingleton<RouteManager>();
        services.AddSingleton<UapiClient>();
        services.AddSingleton<DnsConfigurator>();
        services.AddSingleton<NetworkReconciler>();
        services.AddSingleton<WindowsFirewall>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ConfigRepository>();
        services.AddSingleton<GeoActivator>();
        services.AddSingleton<GeoConfigurator>();
        services.AddSingleton<GeoFileUpdater>();
        services.AddSingleton<GeoUpdateChecker>();
        services.AddSingleton<UpdateChecker>();
        services.AddSingleton<UpdateState>();
        services.AddSingleton<TunnelRunner>();
        services.AddSingleton<ProfileRunner>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<DiagnosticsCollector>();
        services.AddSingleton<AgentStatusBroker>();
        services.AddSingleton<Cli>();
    }
}
