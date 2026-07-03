using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Builds a redacted diagnostics bundle for support (#82): the log files from both the agent and every
/// per-tunnel service process, plus a summary (versions, OS, settings, connection state) and the live
/// journal, zipped into one file the user can send. Secrets - private/preshared keys and basic-auth
/// credentials - are scrubbed so the bundle is safe to share. The agent (SYSTEM) writes it under ProgramData,
/// which the unprivileged UI can read and copy to a user-chosen location.
/// </summary>
internal sealed class DiagnosticsCollector(IStateStore store, SettingsStore settings, LogRingBuffer logBuffer, AgentControl control, ILogger<DiagnosticsCollector> logger)
{
    // Mask the value after a wg-quick "PrivateKey =" / "PresharedKey =" or a UAPI "private_key=" /
    // "preshared_key=". Public keys and endpoints are not secrets and are kept (they help diagnosis).
    private static readonly Regex KeyMaterial =
        new(@"(?i)((?:private|preshared)[_ ]?key\s*[=:]\s*)\S+", RegexOptions.Compiled);

    // Strip basic-auth credentials embedded in a URL (e.g. a wss://user:pass@host WebSocket front).
    private static readonly Regex UrlCredentials =
        new(@"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s:]+:[^/@\s]*@", RegexOptions.Compiled);

    // Strip the path/anti-probe token after the host in a ws/wss URL (with or without leftover userinfo).
    // Scoped to ws/wss so benign https paths (geo source / update URLs) keep their diagnostic value; the
    // WebSocket path prefix is a shared anti-probe secret and must not survive into the bundle.
    private static readonly Regex WsUrlPathToken =
        new(@"(?i)(wss?://(?:[^/@\s]+@)?[^/@\s]+)/\S+", RegexOptions.Compiled);

    // Strip a bare basic-auth credential the wstunnel underlay takes on its command line (or echoes to its
    // own stdout/stderr, captured at Debug), plus generic credential/password labels.
    private static readonly Regex CredentialFlag =
        new(@"(?i)(--http-upgrade-credentials[=\s]+)\S+", RegexOptions.Compiled);
    private static readonly Regex CredentialLabel =
        new(@"(?i)((?:credentials|password|passwd)\s*[=:]\s*)\S+", RegexOptions.Compiled);

    /// <summary>
    /// Writes a diagnostics zip under the diagnostics directory and returns its full path.
    /// </summary>
    public async Task<string> CollectAsync(CancellationToken ct = default)
    {
        var dir = TunnelPaths.DiagnosticsDirectory();
        Directory.CreateDirectory(dir);
        PruneOld(dir);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(dir, $"ageo-diagnostics-{stamp}.zip");
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        var summary = await BuildSummaryAsync(ct);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddText(zip, "summary.txt", Redact(summary));
            AddText(zip, "journal.txt", Redact(string.Join(Environment.NewLine, logBuffer.Snapshot())));

            var logDir = TunnelPaths.LogDirectory();
            if (Directory.Exists(logDir))
            {
                // Both processes roll into "ageo-<date>[_NNN].log" (the agent and each per-tunnel service);
                // agent.log is the legacy name. routes.log[.1] is the optional dedicated routing log (#82).
                // Include them all so a connect failure that only the tunnel process saw, and the route/resolve
                // trail behind a slow-load report, are both in the bundle.
                var files = Directory.EnumerateFiles(logDir, "ageo-*.log")
                    .Concat(Directory.EnumerateFiles(logDir, "agent.log"))
                    .Concat(Directory.EnumerateFiles(logDir, "routes.log*"));
                foreach (var file in files)
                {
                    try
                    {
                        AddRedactedLog(zip, file);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "diagnostics: could not add log {File}", Path.GetFileName(file));
                    }
                }
            }
        }

        logger.LogInformation("diagnostics bundle written: {Path}", zipPath);
        return zipPath;
    }

    private async Task<string> BuildSummaryAsync(CancellationToken ct)
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
        sb.AppendLine($"refresh:         {s.RefreshSeconds}s");
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

        // Per-config MTU and routing (#82): the single most useful thing when diagnosing a "slow handshake /
        // won't route" report is what MTU the tunnel actually uses and how each config splits traffic. Kept
        // structural (no keys/endpoints beyond what redaction leaves) so the block is safe to share.
        var configs = await store.ListConfigNamesAsync();
        sb.AppendLine($"[configs] ({configs.Count})");
        foreach (var config in configs)
        {
            sb.AppendLine($"  {config}:");
            var transport = await store.GetConfigTransportAsync(config, ct);
            var mtu = transport is { Mtu: > 0 } ? transport.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture) : "1280 (default)";
            sb.AppendLine($"    mtu:        {mtu}");
            if (transport?.UseWebSocket == true)
            {
                var wsHost = string.IsNullOrWhiteSpace(transport.WebSocketHost) ? "(endpoint host)" : transport.WebSocketHost;
                sb.AppendLine($"    websocket:  on -> {wsHost}:{transport.WebSocketPort}");
            }
            else
            {
                sb.AppendLine("    websocket:  off (plain UDP)");
            }

            var geoSettings = await store.GetTunnelGeoAsync(config, ct);
            if (geoSettings is not null)
            {
                sb.AppendLine($"    geo:        split={(geoSettings.GeoSplit ? "on" : "off")}, {geoSettings.Rules.Count} rule(s), {geoSettings.Routes.Count} route(s), {geoSettings.Domains.Count} domain(s)");
            }

            var configDns = await store.GetConfigDnsAsync(config, ct);
            sb.AppendLine($"    dns:        {(string.IsNullOrWhiteSpace(configDns?.Servers) ? "auto (system)" : configDns!.Servers)}");

            var configEx = await store.GetConfigExclusionsAsync(config, ct);
            var exCount = string.IsNullOrWhiteSpace(configEx?.Exclusions)
                ? 0
                : configEx!.Exclusions.Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
            sb.AppendLine($"    exclusions: {(configEx is null ? "default (RFC1918 + local subnets)" : $"{exCount} entr(ies)")}");

            var message = await store.GetSettingAsync(TunnelPaths.ConnectMessageKey(config), ct);
            if (!string.IsNullOrWhiteSpace(message))
            {
                sb.AppendLine($"    last error: {message}");
            }
        }

        // Routing lists and how profiles bind them: the routing preset (its own DNS/exclusions/all-UDP) is
        // what actually shapes a matched connect, so a "list-driven" tunnel's behaviour is spelled out here.
        var routingLists = await store.ListRoutingListsAsync(ct);
        if (routingLists.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"[routing lists] ({routingLists.Count})");
            foreach (var list in routingLists)
            {
                sb.AppendLine($"  [{list.Id}] {list.Name}: {list.Rules.Count} rule(s), {list.Routes.Count} route(s), {list.Domains.Count} domain(s)");
            }
        }

        var profiles = await store.ListBalancerNamesAsync(ct);
        if (profiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"[profiles] ({profiles.Count})");
            foreach (var name in profiles)
            {
                var balancer = await store.GetBalancerAsync(name, ct);
                var (listId, useRouting) = await store.GetProfileRoutingAsync(name, ct);
                var routing = listId is not null ? $"routing list {listId} ({(useRouting ? "on" : "off")})" : "no routing list";
                sb.AppendLine($"  {name} -> config '{(string.IsNullOrEmpty(balancer?.Config) ? "(none)" : balancer!.Config)}', {routing}");
            }
        }

        return sb.ToString();
    }

    private void AddRedactedLog(ZipArchive zip, string file)
    {
        var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
        // Share read/write: the current day's file is held open by the Serilog file sink.
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input);
        using var output = new StreamWriter(entry.Open());
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            output.WriteLine(Redact(line));
        }
    }

    private static string Redact(string text)
    {
        text = KeyMaterial.Replace(text, "$1[REDACTED]");
        text = UrlCredentials.Replace(text, "$1[REDACTED]@");
        text = WsUrlPathToken.Replace(text, "$1/[REDACTED]");
        text = CredentialFlag.Replace(text, "$1[REDACTED]");
        text = CredentialLabel.Replace(text, "$1[REDACTED]");
        return text;
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    // Keep the diagnostics folder from growing without bound: drop bundles older than a week. Best-effort.
    private static void PruneOld(string dir)
    {
        try
        {
            var cutoff = DateTimeOffset.Now.AddDays(-7);
            foreach (var old in Directory.EnumerateFiles(dir, "ageo-diagnostics-*.zip"))
            {
                if (File.GetLastWriteTime(old) < cutoff)
                {
                    File.Delete(old);
                }
            }
        }
        catch
        {
            // Pruning is never worth failing a collection over.
        }
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
