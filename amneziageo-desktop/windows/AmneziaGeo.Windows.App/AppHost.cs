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

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(config => config
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(TunnelPaths.LogDirectory(), "ageo-.log"),
                rollingInterval: RollingInterval.Day)
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
        services.AddSingleton<AgentStatusBroker>();
        services.AddSingleton<Cli>();
    }
}
