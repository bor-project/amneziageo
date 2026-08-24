using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Dal;

/// <summary>
/// What the platform lends the archive: a note on a config, the effective configuration it runs on, and the
/// destinations its live tunnel holds. Each is optional and its absence is stated in the archive.
/// </summary>
public sealed record BundleSources(
    Func<string, CancellationToken, Task<string?>>? Note = null,
    Func<CancellationToken, Task<string>>? Runtime = null,
    Func<CancellationToken, Task<string>>? Cache = null);

/// <summary>
/// Builds a redacted diagnostics archive for support: the library summary, the effective configuration, the live
/// cache, the diagnostic runs and both log tables.
/// </summary>
public sealed class DiagnosticsBundle(IStateStore store, SqliteLogStore logs)
{
    // Mask private/preshared key values; public keys and endpoints stay for diagnosis.
    private static readonly Regex KeyMaterial =
        new(@"(?i)((?:private|preshared)[_ ]?key\s*[=:]\s*)\S+");

    // Strip basic-auth credentials embedded in a URL.
    private static readonly Regex UrlCredentials =
        new(@"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s:]+:[^/@\s]*@");

    // Strip the path/anti-probe token after the host in a ws/wss URL.
    private static readonly Regex WsUrlPathToken =
        new(@"(?i)(wss?://(?:[^/@\s]+@)?[^/@\s]+)/\S+");

    // Strip wstunnel credential flags and generic credential/password labels.
    private static readonly Regex CredentialFlag =
        new(@"(?i)(--http-upgrade-credentials[=\s]+)\S+");
    private static readonly Regex CredentialLabel =
        new(@"(?i)((?:credentials|password|passwd)\s*[=:]\s*)\S+");

    // The structured log tables and their file names inside the archive.
    private static readonly (string Table, string Entry)[] LogTables =
        [(SqliteLogStore.AgentTable, "ageo.log"), (SqliteLogStore.RoutesTable, "routes.log"),
         (SqliteLogStore.ChecksTable, "checks.log"), (SqliteLogStore.ProbeTable, "probe.log")];

    /// <summary>
    /// Writes a diagnostics zip into a directory and returns its full path. The header opens the summary the
    /// platform builds for itself; the rest of the archive is what support has to read together - the effective
    /// configuration, the live cache, the diagnostic runs and both logs.
    /// </summary>
    public async Task<string> WriteAsync(
        string directory,
        string header,
        Func<LogRow, string> render,
        BundleSources? sources = null,
        CancellationToken ct = default)
    {
        var parts = sources ?? new BundleSources();
        Directory.CreateDirectory(directory);
        PruneOld(directory);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(directory, $"ageo-diagnostics-{stamp}.zip");
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        var summary = header + await LibraryAsync(parts.Note, ct).ConfigureAwait(false);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddText(zip, "summary.txt", Redact(summary));
            AddText(zip, "config.txt", Redact(await SectionAsync(parts.Runtime, "the effective configuration", ct).ConfigureAwait(false)));
            AddText(zip, "cache.txt", Redact(await SectionAsync(parts.Cache, "the live routing cache", ct).ConfigureAwait(false)));

            foreach (var (table, entryName) in LogTables)
            {
                var temp = Path.Combine(directory, entryName);
                try
                {
                    await logs.ExportAsync(table, temp, row => Redact(render(row)), ct).ConfigureAwait(false);
                    zip.CreateEntryFromFile(temp, entryName, CompressionLevel.Optimal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    AddText(zip, entryName, $"the '{table}' log could not be read: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        return zipPath;
    }

    // One archive entry the platform renders for itself; a failure becomes the entry's text, never a lost archive.
    private static async Task<string> SectionAsync(Func<CancellationToken, Task<string>>? source, string what, CancellationToken ct)
    {
        if (source is null)
        {
            return $"{what} is not available on this system";
        }

        try
        {
            return await source(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            return $"{what} could not be read: {ex.Message}";
        }
    }

    /// <summary>
    /// Masks key material and credentials in a text.
    /// </summary>
    public static string Redact(string text)
    {
        text = KeyMaterial.Replace(text, "$1[REDACTED]");
        text = UrlCredentials.Replace(text, "$1[REDACTED]@");
        text = WsUrlPathToken.Replace(text, "$1/[REDACTED]");
        text = CredentialFlag.Replace(text, "$1[REDACTED]");
        text = CredentialLabel.Replace(text, "$1[REDACTED]");
        return text;
    }

    // The stored library: every config with its transport, geo, dns and exclusions, then the routing lists.
    private async Task<string> LibraryAsync(Func<string, CancellationToken, Task<string?>>? note, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var configs = await store.ListConfigNamesAsync(ct).ConfigureAwait(false);
        sb.AppendLine($"[configs] ({configs.Count})");
        foreach (var config in configs)
        {
            sb.AppendLine($"  {config}:");
            var transport = await store.GetConfigTransportAsync(config, ct).ConfigureAwait(false);
            var mtu = transport is { Mtu: > 0 } ? transport.Mtu.ToString(CultureInfo.InvariantCulture) : "default";
            sb.AppendLine($"    mtu:        {mtu}");
            if (transport?.UseWebSocket == true)
            {
                var host = string.IsNullOrWhiteSpace(transport.WebSocketHost) ? "(endpoint host)" : transport.WebSocketHost;
                sb.AppendLine($"    websocket:  on -> {host}:{transport.WebSocketPort}");
            }
            else
            {
                sb.AppendLine("    websocket:  off (plain UDP)");
            }

            sb.AppendLine($"    ipv6:       {(transport?.UseIpv6 == true ? "on" : "off")}");

            var geo = await store.GetTunnelGeoAsync(config, ct).ConfigureAwait(false);
            if (geo is not null)
            {
                sb.AppendLine($"    geo:        split={(geo.GeoSplit ? "on" : "off")}, {geo.Rules.Count} rule(s), {geo.Routes.Count} route(s), {geo.Domains.Count} domain(s)");
            }

            var dns = await store.GetConfigDnsAsync(config, ct).ConfigureAwait(false);
            sb.AppendLine($"    dns:        {(string.IsNullOrWhiteSpace(dns?.Servers) ? "auto (system)" : dns!.Servers)}");

            var exclusions = await store.GetConfigExclusionsAsync(config, ct).ConfigureAwait(false);
            var count = string.IsNullOrWhiteSpace(exclusions?.Exclusions)
                ? 0
                : exclusions!.Exclusions.Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
            sb.AppendLine($"    exclusions: {(exclusions is null ? "default (RFC1918 + local subnets)" : $"{count} entr(ies)")}");

            var binding = await store.GetConfigRoutingAsync(config, ct).ConfigureAwait(false);
            var routing = binding is null
                ? "default list"
                : binding.RoutingListId is { } boundList ? $"list {boundList}" : "off (no list)";
            sb.AppendLine($"    routing:    {routing}");

            var line = note is null ? null : await note(config, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine($"    last error: {line}");
            }
        }

        var lists = await store.ListRoutingListsAsync(ct).ConfigureAwait(false);
        if (lists.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"[routing lists] ({lists.Count})");
            foreach (var list in lists)
            {
                sb.AppendLine($"  [{list.Id}] {list.Name}: {list.Rules.Count} rule(s), {list.Routes.Count} route(s), {list.Domains.Count} domain(s)");
            }
        }

        var selected = await store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false);
        sb.AppendLine();
        sb.AppendLine($"[routing] default {(selected is null ? "off (no list)" : $"list {selected}")}");

        var sources = await store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        if (sources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"[geo sources] ({sources.Count})");
            foreach (var source in sources)
            {
                var meta = await store.GetGeoFileAsync(source.Name, ct).ConfigureAwait(false);
                sb.AppendLine($"  {source.Name} ({source.Kind}): {meta?.CategoryCount ?? 0} categor(ies), updated {meta?.UpdatedAt.ToString("u", CultureInfo.InvariantCulture) ?? "never"}");
            }
        }

        return sb.ToString();
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    // Drop bundles older than a week.
    private static void PruneOld(string directory)
    {
        try
        {
            var cutoff = DateTimeOffset.Now.AddDays(-7);
            foreach (var old in Directory.EnumerateFiles(directory, "ageo-diagnostics-*.zip"))
            {
                if (File.GetLastWriteTime(old) < cutoff)
                {
                    File.Delete(old);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruning is never worth failing a collection over.
        }
    }
}
