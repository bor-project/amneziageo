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

        // In-memory log ring for the UI activity journal, fed by the Serilog sink below. Only the agent serves it
        // (AgentStatusBroker / DiagnosticsCollector); a transient per-tunnel process has no reader, so skip it there.
        var logBuffer = agentTarget is not null ? new LogRingBuffer() : null;
        if (logBuffer is not null)
        {
            builder.Services.AddSingleton(logBuffer);
        }

        // Live verbosity switch: shared by both processes, kept in sync with the "log-level" setting.
        var logLevel = new LogLevelController();
        builder.Services.AddSingleton(logLevel);

        // Resettable file sink so the agent log can be cleared at runtime; shared as a singleton for the clear command.
        // Pinned greppable line format: timestamp + 3-char level + source + message, one entry per line
        // (exceptions append on following lines). Explicit so the on-disk format the log viewer and grep rely
        // on cannot drift with Serilog's default template.
        var logFileSink = new ResettableFileSink(
            Path.Combine(TunnelPaths.LogDirectory(), "ageo.log"),
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Source:l} {Message:lj}{NewLine}{Exception}");
        builder.Services.AddSingleton(logFileSink);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(config =>
        {
            config.MinimumLevel.ControlledBy(logLevel.Switch)
                .Enrich.FromLogContext()
                // Source column: the logger's class name, derived from SourceContext.
                .Enrich.With(new LogSourceEnricher())
                .WriteTo.Console()
                .WriteTo.Sink(logFileSink);
            if (logBuffer is not null)
            {
                config.WriteTo.Sink(new RingBufferSink(logBuffer));
            }
        });

        RegisterServices(builder.Services);

        if (agentTarget is not null)
        {
            builder.Services.AddWindowsService(options => options.ServiceName = TunnelPaths.AgentServiceName());
            builder.Services.AddSingleton(new AgentTarget(agentTarget));
            builder.Services.AddHostedService<AgentBackgroundService>();
            builder.Services.AddHostedService<NetworkWatcher>();
            builder.Services.AddHostedService<StatusPipeServer>();
            builder.Services.AddHostedService<UpdateCheckService>();
            builder.Services.AddHostedService<GeoUpdateCheckService>();
            builder.Services.AddHostedService<GeoBootstrapService>();
            builder.Services.AddHostedService<LogLevelBackgroundWatcher>();
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
