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

        // Capture recent log lines in memory so the UI home screen can show a live activity journal;
        // the same instance is registered for the status broker and fed by the Serilog sink below.
        var logBuffer = new LogRingBuffer();
        builder.Services.AddSingleton(logBuffer);

        // The live verbosity switch (#82): the minimum level is not hardcoded but obeys a switch the user can
        // raise to Debug/Trace to diagnose a failing or slow connect. Shared by both processes and kept in
        // sync with the persisted "log-level" setting by LogLevelWatcher; the broker also pushes it instantly.
        var logLevel = new LogLevelController();
        builder.Services.AddSingleton(logLevel);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(config => config
            .MinimumLevel.ControlledBy(logLevel.Switch)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(TunnelPaths.LogDirectory(), "ageo-.log"),
                rollingInterval: RollingInterval.Day,
                // Bound the on-disk footprint so a long Trace session cannot fill the disk: roll at 25 MB and
                // keep at most 10 files per process. Trace is verbose, so this cap is what makes it safe to
                // leave on while reproducing an issue.
                fileSizeLimitBytes: 25_000_000,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 10)
            .WriteTo.Sink(new RingBufferSink(logBuffer)));

        RegisterServices(builder.Services);

        if (agentTarget is not null)
        {
            builder.Services.AddWindowsService(options => options.ServiceName = TunnelPaths.AgentServiceName());
            builder.Services.AddSingleton(new AgentTarget(agentTarget));
            builder.Services.AddHostedService<AgentBackgroundService>();
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
        services.AddSingleton<BalancerRunner>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<DiagnosticsCollector>();
        services.AddSingleton<AgentStatusBroker>();
        services.AddSingleton<Cli>();
    }
}
