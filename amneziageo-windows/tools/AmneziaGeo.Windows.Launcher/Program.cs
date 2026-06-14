using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Dev launcher entry point: starts the configured backend and UI in one step.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(config => config
            .MinimumLevel.Information()
            .WriteTo.Console());

        builder.Services.AddSingleton<Launcher>();

        using (var host = builder.Build())
        {
            var launcher = host.Services.GetRequiredService<Launcher>();
            return await launcher.RunAsync(args);
        }
    }
}
