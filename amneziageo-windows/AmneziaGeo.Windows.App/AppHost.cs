using System.Net.Http;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Windows.App.Fleet;
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
    public static IHost Build(string? agentTarget, string userRoot)
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

        RegisterServices(builder.Services, userRoot);

        if (agentTarget is not null)
        {
            // Retention cap lives in logs\settings.json; only the agent prunes (per-tunnel processes just insert).
            builder.Services.AddSingleton(LogSettings.LoadOrCreate(TunnelPaths.LogSettingsFile()));
            builder.Services.AddWindowsService(options => options.ServiceName = TunnelPaths.AgentServiceName());
            builder.Services.AddSingleton(new AgentTarget(agentTarget));
            builder.Services.AddSingleton<AgentMode>();
            builder.Services.AddSingleton<FleetControl>();
            builder.Services.AddSingleton<FleetStore>();
            builder.Services.AddSingleton<FleetRunnerFactory>();

            // One broker answers the window either way: with the flag off it is the one registered below it, to
            // the line, and the mode's own requests are refused as requests it has no handler for.
            builder.Services.AddSingleton<FleetStatusBroker>();
            builder.Services.AddSingleton<AgentStatusBroker>(sp => sp.GetRequiredService<FleetStatusBroker>());

            // The resolver watch asks who holds the lookups, and in the mode that is the tunnel carrying the
            // machine rather than the machine's own lamp.
            builder.Services.AddSingleton<FleetResolverHolder>();
            builder.Services.AddSingleton<ResolverHolder>(sp => sp.GetRequiredService<FleetResolverHolder>());

            // The mode is the fork, and this is the only object that sees it: it raises the supervisor the flag
            // calls for and changes it over when the flag moves. Everything below is wired the same either way.
            builder.Services.AddHostedService<ModeSwitchService>();
            builder.Services.AddHostedService<NetworkWatcher>();
            builder.Services.AddHostedService<StatusPipeServer>();
            builder.Services.AddHostedService<UpdateCheckService>();
            builder.Services.AddHostedService<GeoUpdateCheckService>();
            builder.Services.AddHostedService<GeoBootstrapService>();
            builder.Services.AddHostedService<DnsHealthService>();
            builder.Services.AddHostedService<LogLevelBackgroundWatcher>();
            builder.Services.AddHostedService<LogMaintenanceService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalProxyService>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<WindowsHotspotService>());
        }

        return builder.Build();
    }

    private static void RegisterServices(IServiceCollection services, string userRoot)
    {
        services.AddSingleton<UserStoreRegistry>();
        // Shared machine store: geo sources/files and machine settings.
        services.AddSingleton(_ => new SqliteStateStore(TunnelPaths.MachineDbFile()));
        services.AddSingleton(sp => new ScopedStoreFactory(sp.GetRequiredService<SqliteStateStore>(), sp.GetRequiredService<UserStoreRegistry>()));
        services.AddSingleton<ActiveTunnelScope>();
        // Default composite store for this process: the machine store paired with this session's user library.
        services.AddSingleton<IStateStore>(sp => sp.GetRequiredService<ScopedStoreFactory>().For(userRoot));
        services.AddSingleton<AgentControl>();
        // Single tunnel: it holds every duty. The agent replaces this with the set's own arbiter when the mode is on.
        services.AddSingleton<TunnelDutyRoster>();
        services.AddSingleton<ResolverHolder>();
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
        services.AddSingleton<IGeoFileStore, WindowsGeoFileStore>();
        services.AddSingleton<GeoConfigurator>();
        services.AddSingleton<GeoHttp>();
        services.AddSingleton<GeoFileUpdater>();
        services.AddSingleton<GeoUpdateChecker>();
        services.AddSingleton<UpdateChecker>();
        services.AddSingleton<UpdateState>();
        services.AddSingleton<LiveSession>();
        services.AddSingleton<TunnelRunner>();
        services.AddSingleton<RuntimeInspector>();
        services.AddSingleton<ConfigRunner>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<DiagnosticsCollector>();
        services.AddSingleton<FleetLive>();
        services.AddSingleton<CheckService>();
        services.AddSingleton<LocalProxyService>();
        services.AddSingleton<WindowsHotspotService>();
        services.AddSingleton<AgentStatusBroker>();
        services.AddSingleton<Cli>();
    }
}
