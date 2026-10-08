using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Dal;

/// <summary>
/// What the platform lends the archive: a note on a config, the effective configuration it runs on, the
/// destinations its live tunnel holds, the journal rows it keeps in memory and the library it keeps outside the
/// store. Each is optional and its absence is stated in the archive.
/// </summary>
public sealed record BundleSources(
    Func<string, CancellationToken, Task<string?>>? Note = null,
    Func<CancellationToken, Task<string>>? Runtime = null,
    Func<CancellationToken, Task<string>>? Cache = null,
    Func<CancellationToken, Task<IReadOnlyList<LogRow>>>? Recent = null,
    BundleLibrary? Library = null);

/// <summary>
/// The configurations and the list choice of a platform that keeps them outside the store.
/// </summary>
public sealed record BundleLibrary(IReadOnlyList<string> Names, Func<string, string?> Text, long? SelectedList);

/// <summary>
/// Builds a redacted diagnostics archive for support: the library summary, the effective configuration, the live
/// cache, the journal rows kept in memory, the diagnostic runs and the log tables.
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
        [(SqliteLogStore.AgentTable, "ageo.log"), (SqliteLogStore.DnsTable, "dns.log"),
         (SqliteLogStore.RoutesTable, "routes.log"), (SqliteLogStore.ChecksTable, "checks.log"),
         (SqliteLogStore.ProbeTable, "probe.log")];

    // Rules of one routing list printed at most.
    private const int MaxRules = 200;

    /// <summary>
    /// Writes a diagnostics zip into a directory and returns its full path. The header opens the summary the
    /// platform builds for itself; the rest of the archive is what support has to read together - the effective
    /// configuration, the live cache, the journal rows kept in memory, the diagnostic runs and the logs.
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

        var summary = header + await LibraryAsync(parts.Note, parts.Library, ct).ConfigureAwait(false);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddText(zip, "summary.txt", Redact(summary));
            AddText(zip, "config.txt", Redact(await SectionAsync(parts.Runtime, "the effective configuration", ct).ConfigureAwait(false)));
            AddText(zip, "cache.txt", Redact(await SectionAsync(parts.Cache, "the live routing cache", ct).ConfigureAwait(false)));
            AddText(zip, "recent.log", Redact(await RecentAsync(parts.Recent, render, ct).ConfigureAwait(false)));

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

    // The journal rows the platform keeps in memory, one per line, oldest first.
    private static async Task<string> RecentAsync(Func<CancellationToken, Task<IReadOnlyList<LogRow>>>? source, Func<LogRow, string> render, CancellationToken ct)
    {
        if (source is null)
        {
            return "the journal rows kept in memory are not available on this system";
        }

        var text = new StringBuilder();
        foreach (var row in await source(ct).ConfigureAwait(false))
        {
            text.Append(render(row)).Append('\n');
        }

        return text.ToString();
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

    // The library: every config with its transport, geo, dns and exclusions, then the routing lists with their rules.
    private async Task<string> LibraryAsync(Func<string, CancellationToken, Task<string?>>? note, BundleLibrary? library, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var configs = library?.Names ?? await store.ListConfigNamesAsync(ct).ConfigureAwait(false);
        sb.AppendLine($"[configs] ({configs.Count})");
        foreach (var config in configs)
        {
            sb.AppendLine($"  {config}:");
            var transport = await store.GetConfigTransportAsync(config, ct).ConfigureAwait(false);
            var text = library is null ? await store.GetConfigTextAsync(config, ct).ConfigureAwait(false) : library.Text(config);
            var offer = await ServerOfferStore.ReadAsync(store, config, text, ct).ConfigureAwait(false);
            var mtu = transport is { Mtu: > 0 } ? transport.Mtu.ToString(CultureInfo.InvariantCulture) : "default";
            sb.AppendLine($"    mtu:        {mtu}");
            sb.AppendLine($"    server:     {(offer.Ours ? $"ours {offer.Version}, offers {string.Join(", ", offer.Features.Keys)}" : "not ours or not asked")}");
            if (transport?.UseWebSocket == true)
            {
                sb.AppendLine(WsEndpoint.Of(text, offer, transport) is { } front
                    ? $"    websocket:  on -> {front.Display()} ({WsEndpoint.SourceOf(text, offer).ToString().ToLowerInvariant()})"
                    : "    websocket:  on, the server offers no front");
            }
            else
            {
                sb.AppendLine("    websocket:  off (plain UDP)");
            }

            sb.AppendLine($"    ipv6:       {(transport?.UseIpv6 == true ? "on" : "off")}");
            sb.AppendLine($"    router:     {(transport?.UseRouter != false ? "on" : "off")}");
            sb.AppendLine($"    routing:    {(transport?.UseRouting != false ? "on" : "off")}");
            sb.AppendLine($"    inbound:    {(transport?.AllowInbound != true ? "off" : transport.InboundNetwork ? "tunnel network" : "server only")}");

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
                foreach (var rule in list.Rules.Take(MaxRules))
                {
                    sb.AppendLine($"    {RuleText(rule)}");
                }

                if (list.Rules.Count > MaxRules)
                {
                    sb.AppendLine($"    ... {list.Rules.Count - MaxRules} more rule(s)");
                }
            }
        }

        var selected = library is null ? await store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false) : library.SelectedList;
        sb.AppendLine();
        sb.AppendLine($"[routing] {(selected is null ? "off (no list)" : $"list {selected}")}");

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

    // A rule in the form the lists are written in: its role, its kind and its value.
    private static string RuleText(GeoRule rule)
    {
        var kind = rule.Kind switch
        {
            GeoRuleKind.GeoSite => "geosite",
            GeoRuleKind.GeoIp => "geoip",
            GeoRuleKind.Domain => "domain",
            GeoRuleKind.App => "app",
            _ => "cidr",
        };
        return $"{rule.Role.ToString().ToLowerInvariant()}|{kind}:{rule.Value}";
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
