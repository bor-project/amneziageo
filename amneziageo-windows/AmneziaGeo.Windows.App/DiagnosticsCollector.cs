using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Builds a redacted diagnostics bundle for support.
/// </summary>
internal sealed class DiagnosticsCollector(IStateStore store, SettingsStore settings, SqliteLogStore logStore, AgentControl control, RuntimeInspector inspector, ILogger<DiagnosticsCollector> logger)
{
    private readonly DiagnosticsBundle _bundle = new(store, logStore);

    /// <summary>
    /// Writes a diagnostics zip under the diagnostics directory and returns its full path.
    /// </summary>
    public async Task<string> CollectAsync(CancellationToken ct = default)
    {
        var header = await HeaderAsync(ct);
        var target = control.RunningTarget ?? control.Target ?? string.Empty;
        var zipPath = await _bundle.WriteAsync(
            TunnelPaths.DiagnosticsDirectory(),
            header,
            LogFormat.Render,
            new BundleSources(
                (config, token) => store.GetSettingAsync(TunnelPaths.ConnectMessageKey(config), token),
                token => RuntimeAsync(target, token),
                _ => Task.FromResult(target.Length == 0 ? "no configuration is selected" : inspector.HeldText(target))),
            ct);

        logger.LogInformation("the diagnostics archive is ready at {Path}; keys and addresses in it are masked, so it can be sent for support", zipPath);
        return zipPath;
    }

    // The configuration the tunnel runs on, or would run on at the next connect; keys are masked by the renderer.
    private async Task<string> RuntimeAsync(string config, CancellationToken ct)
    {
        return config.Length == 0
            ? "no configuration is selected"
            : await inspector.RenderAsync(store, config, control.Running, ct);
    }

    private async Task<string> HeaderAsync(CancellationToken ct)
    {
        var s = await settings.LoadAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("AmneziaGeo diagnostics");
        sb.AppendLine($"generated:       {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"app version:     {AppVersion()}");
        sb.AppendLine($"engine version:  {AppSettings.EngineVersion}");
        sb.AppendLine($"os:              {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"runtime:         {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine();
        sb.AppendLine("[settings]");
        sb.AppendLine($"log level:       {s.LogLevel}");
        sb.AppendLine($"routing log:     {(s.RouteLog ? "on" : "off")}");
        sb.AppendLine($"route ttl:       {s.RouteTtlSeconds}s");
        sb.AppendLine($"connect timeout: {s.ConnectTimeoutSeconds}s");
        sb.AppendLine($"dead threshold:  {s.DeadThresholdSeconds}s");
        sb.AppendLine($"geo auto-check:  {s.GeoAutoCheck} (interval {s.GeoCheckIntervalHours}h, validity {s.GeoCacheValidityHours}h)");
        sb.AppendLine($"all-udp:         {s.TunnelAllUdp}");
        sb.AppendLine();
        sb.AppendLine("[state]");
        sb.AppendLine($"selected target: {control.Target ?? "-"}");
        sb.AppendLine($"running:         {control.Running}");
        sb.AppendLine($"connect failed:  {control.ConnectFailed}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string AppVersion()
    {
        var assembly = typeof(DiagnosticsCollector).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "?"
            : informational;
    }
}
