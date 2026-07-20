using AmneziaGeo.Windows.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Dev launcher entry point: hosts the backend and UI in-process in one step.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--service", _])
        {
            return AppEntry.RunAsync(args).GetAwaiter().GetResult();
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Development,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(config => config
            .MinimumLevel.Information()
            .WriteTo.Console());

        builder.Services.Configure<LauncherOptions>(builder.Configuration.GetSection("Launcher"));
        builder.Services.AddSingleton<Launcher>();

        using (var host = builder.Build())
        {
            return host.Services.GetRequiredService<Launcher>().Run(args);
        }
    }
}
