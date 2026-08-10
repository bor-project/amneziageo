using System.Globalization;
using System.Text;
using System.Text.Json;

using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Renders the configuration a tunnel runs on - or would run on at the next connect - and the cached values
/// behind it. Reads the live device over UAPI while the tunnel is up.
/// </summary>
internal sealed class RuntimeInspector(SettingsStore settings, UapiClient uapi, LiveSession session, WindowsFirewall firewall, ILogger<RuntimeInspector> logger)
{
    // Cache rows returned at most; the tail is dropped and the total reported.
    private const int MaxCacheEntries = 20000;

    // Destinations one session report lists; the rest is counted, not named.
    private const int MaxSessionRows = AmneziaGeo.Ipc.SessionReport.MaxRows;

    // Prefixes printed at most, per config line and per device peer; a geo split puts thousands on one tunnel.
    private const int MaxAllowedIps = 500;

    // Stands in for anything that must not leave the agent.
    private const string Masked = "***";

    private const int KeyWidth = 18;

    /// <summary>
    /// A cached value: which cache holds it, its key and its content.
    /// </summary>
    public sealed record CacheEntry(string Kind, string Key, string Value);

    /// <summary>
    /// Cached values with the total held before the cap.
    /// </summary>
    public sealed record CacheSnapshot(int Total, bool Capped, IReadOnlyList<CacheEntry> Entries);

    /// <summary>
    /// Renders the effective configuration report. Applied reads the live device, otherwise the report covers
    /// what the next connect would use.
    /// </summary>
    public async Task<string> RenderAsync(IStateStore store, string config, bool applied, CancellationToken ct)
    {
        var text = new StringBuilder();
        var configText = await store.GetConfigTextAsync(config, ct) ?? string.Empty;
        var transport = await store.GetConfigTransportAsync(config, ct);
        var geo = await store.GetActiveTunnelGeoAsync(config, ct);
        var listId = await store.GetActiveRoutingListIdAsync(config, ct);
        var list = listId is long bucketId ? await store.GetRoutingListAsync(bucketId, ct) : null;
        var routing = listId is long settingsId ? await store.GetRoutingSettingsAsync(settingsId, ct) : null;
        var materialization = await store.GetActiveRoutingListMaterializationAsync(config, ct);
        var app = await settings.LoadAsync(ct);
        var split = list is not null ? !(routing?.UseGlobalProxy ?? false) : geo?.GeoSplit ?? false;
        var device = applied ? ReadDevice(config) : null;

        Section(text, "state");
        Row(text, "source", applied ? "applied" : "planned");
        Row(text, "config", config);

        Section(text, "routing");
        Row(text, "mode", split ? "split" : "global proxy");
        Row(text, "routing list", list is null ? "-" : $"{list.Name} (id {list.Id})");
        Row(text, "generation", materialization is null ? "-" : materialization.Generation.ToString(CultureInfo.InvariantCulture));
        Row(text, "all udp", (split && (routing?.AllUdp ?? app.TunnelAllUdp)) ? "on" : "off");
        Row(text, "proxy", $"{geo?.Routes.Count ?? 0} ranges, {geo?.Domains.Count ?? 0} domains");
        Row(text, "direct", $"{list?.DirectRoutes.Count ?? 0} ranges, {list?.DirectDomains.Count ?? 0} domains");
        Row(text, "block", $"{list?.BlockRoutes.Count ?? 0} ranges, {list?.BlockDomains.Count ?? 0} domains");
        Row(text, "resolution", "on demand (nothing materialized at connect)");
        Row(text, "route ttl", $"{session.Cache?.TtlSeconds ?? app.RouteTtlSeconds} s idle");
        Row(text, "cache", Held(config, applied));
        Row(text, "apps", (geo?.Apps.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        var exclusions = routing?.Exclusions ?? (await store.GetConfigExclusionsAsync(config, ct))?.Exclusions;
        Row(text, "exclusions", Oneline(exclusions));

        Section(text, "transport");
        Row(text, "carrier", Carrier(configText, transport));
        Row(text, "mtu", TunnelRunner.EffectiveMtu(transport?.Mtu ?? 0).ToString(CultureInfo.InvariantCulture));
        Row(text, "ipv6", (transport?.UseIpv6 ?? false) ? "on" : "off");
        Row(text, "endpoint", WgConfigEditor.GetEndpoint(configText) ?? "-");
        Row(text, "config dns", Join(WgConfigEditor.GetDns(configText)));
        Row(text, "config allowed", Join(WgConfigEditor.GetAllowedIps(configText)));

        Section(text, $"config {config}");
        text.Append(Effective(configText, transport, geo, split, device));
        text.Append('\n');

        if (applied)
        {
            Section(text, "device");
            if (device is null)
            {
                Row(text, "device", "unreachable");
            }
            else
            {
                AppendDevice(text, device);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Collects the destinations the running tunnel holds right now: one row per live verdict, with the idle time
    /// left on it. Nothing persisted and nothing merely materialized belongs here - those are rules, not cache.
    /// </summary>
    /// <summary>
    /// Whether this process is the one running the tunnel; the agent process holds no caches of its own.
    /// </summary>
    public bool HasLiveSession => session.Cache is not null || session.Tracker is not null;

    /// <summary>
    /// How much the running tunnel holds right now.
    /// </summary>
    public sealed record LiveCounts(int Entries, int Routed, int Domains, bool DropWatch, int DropEvents);

    /// <summary>
    /// Live counts of this process's tunnel; zeroes when it runs none.
    /// </summary>
    public LiveCounts Counts()
    {
        var drops = firewall.DropWatch;
        return new LiveCounts(session.Cache?.Size ?? 0, session.Cache?.Active ?? 0,
            session.Tracker?.Snapshot().Count ?? 0, drops.Watching, drops.Events);
    }

    /// <summary>
    /// The destinations the running tunnel holds: read from this process when it owns the tunnel, otherwise from
    /// the service that does.
    /// </summary>
    public CacheSnapshot Held(string config)
    {
        if (HasLiveSession)
        {
            return Collect();
        }

        var served = RuntimeSnapshotPipe.Send(config, RuntimeSnapshotPipe.OpSnapshot, logger);
        if (served is { Length: > 0 })
        {
            try
            {
                return JsonSerializer.Deserialize<CacheSnapshot>(served) ?? Nothing;
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "runtime cache: unreadable reply for {Tunnel}", config);
            }
        }

        return Nothing;
    }

    /// <summary>
    /// The same, rendered one row per line for the support archive.
    /// </summary>
    public string HeldText(string config)
    {
        var snapshot = Held(config);
        var text = new StringBuilder();
        foreach (var entry in snapshot.Entries)
        {
            text.Append(entry.Kind.PadRight(10)).Append(entry.Key.PadRight(22)).Append(entry.Value).Append('\n');
        }

        if (snapshot.Capped)
        {
            text.Append("cut to ").Append(snapshot.Entries.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" of ").Append(snapshot.Total.ToString(CultureInfo.InvariantCulture)).Append(" entries\n");
        }

        return text.Length == 0 ? "the tunnel holds nothing right now" : text.ToString();
    }

    /// <summary>
    /// What the tunnel carries right now, freshest first. Nothing here relays connections, so a destination is
    /// the verdict held for it and the idle clock left on it, and no row counts bytes.
    /// </summary>
    public AmneziaGeo.Ipc.SessionReport Sessions()
    {
        var rows = new List<AmneziaGeo.Ipc.LiveSession>();
        foreach (var domain in session.Tracker?.Snapshot() ?? [])
        {
            rows.Add(new AmneziaGeo.Ipc.LiveSession(domain.Domain, "proxy", IdleSeconds: domain.IdleSeconds));
        }

        var undecided = 0;
        foreach (var held in session.Cache?.Snapshot() ?? [])
        {
            var verdict = Verdict(held.Verdict.ToString());
            if (verdict == AmneziaGeo.Ipc.LiveSession.Undecided)
            {
                undecided++;
            }

            // An adopted address leaves with the name that resolved it, and that name is already a row here.
            if (!held.Adopted)
            {
                rows.Add(new AmneziaGeo.Ipc.LiveSession(held.Address.ToString(), verdict, IdleSeconds: held.IdleSeconds));
            }
        }

        return new AmneziaGeo.Ipc.SessionReport(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [.. rows.OrderBy(row => row.IdleSeconds).Take(MaxSessionRows)],
            rows.Count,
            undecided);
    }

    /// <summary>
    /// The same, read from the service process when the tunnel runs there.
    /// </summary>
    public AmneziaGeo.Ipc.SessionReport HeldSessions(string config)
    {
        if (HasLiveSession)
        {
            return Sessions();
        }

        var served = RuntimeSnapshotPipe.Send(config, RuntimeSnapshotPipe.OpSessions, logger);
        return served is { Length: > 0 }
            ? AmneziaGeo.Ipc.SessionReport.Parse(served)
            : AmneziaGeo.Ipc.SessionReport.Empty;
    }

    // The word a session row carries: an address no rule names is undecided, not "none".
    private static string Verdict(string name)
    {
        return name == "None" ? AmneziaGeo.Ipc.LiveSession.Undecided : name.ToLowerInvariant();
    }

    // A cache nobody answered for.
    private static readonly CacheSnapshot Nothing = new(0, false, []);

    public CacheSnapshot Collect()
    {
        var entries = new List<CacheEntry>();
        var total = 0;

        // Nothing here belongs to the tunnel when it runs elsewhere; reporting this process's empty state as its
        // own would read as a diagnosis of the tunnel.
        if (!HasLiveSession)
        {
            Add(entries, ref total, "state", "cache", "the tunnel service did not answer; nothing to read in this process");
            return new CacheSnapshot(total, false, entries);
        }

        var cache = session.Cache;
        var tracked = session.Tracker?.Snapshot();

        // State first: an empty body otherwise says nothing about whether there is a session to read at all.
        var drops = firewall.DropWatch;
        Add(entries, ref total, "state", "cache", cache is null
            ? "no routing cache"
            : $"{cache.Size} entries, {cache.Active} routed, ttl {cache.TtlSeconds} s");
        Add(entries, ref total, "state", "domains", tracked is null ? "no domain tracker" : $"{tracked.Count} tracked");
        Add(entries, ref total, "state", "drop watch", drops.Watching ? $"on, {drops.Events} events" : "off");

        // Which name holds each adopted address, so its row carries the clock it actually leaves on.
        var owners = new Dictionary<string, (string Domain, int IdleSeconds, int TtlSeconds)>(StringComparer.Ordinal);
        foreach (var domain in tracked ?? [])
        {
            foreach (var ip in domain.Ips)
            {
                if (!owners.TryGetValue(ip, out var known) || domain.IdleSeconds < known.IdleSeconds)
                {
                    owners[ip] = (domain.Domain, domain.IdleSeconds, domain.TtlSeconds);
                }
            }
        }

        foreach (var held in (cache?.Snapshot() ?? []).OrderBy(entry => entry.IdleSeconds))
        {
            // An adopted address leaves with the name that resolved it, so it reports that name's clock.
            var value = held.Adopted && owners.TryGetValue(held.Address.ToString(), out var owner)
                ? $"held by {owner.Domain}, idle {owner.IdleSeconds} s, expires in {owner.TtlSeconds - owner.IdleSeconds} s"
                : $"{(held.Routed ? "routed" : "verdict only")}, idle {held.IdleSeconds} s, expires in {held.TtlSeconds - held.IdleSeconds} s";
            Add(entries, ref total, held.Verdict.ToString().ToLowerInvariant(), held.Address.ToString(), value);
        }

        foreach (var domain in (tracked ?? []).OrderBy(entry => entry.Domain, StringComparer.Ordinal))
        {
            Add(entries, ref total, "domain", domain.Domain,
                $"routed, idle {domain.IdleSeconds} s, expires in {domain.TtlSeconds - domain.IdleSeconds} s, {Join(domain.Ips)}");
        }

        return new CacheSnapshot(total, total > entries.Count, entries);
    }

    // What the running tunnel holds. Read from this process when it runs the tunnel, otherwise from the service
    // process over the runtime pipe.
    private string Held(string config, bool applied)
    {
        if (!applied)
        {
            return "-";
        }

        var counts = HasLiveSession ? Counts() : Remote(config);
        if (counts is null)
        {
            return "tunnel service did not answer";
        }

        var drops = counts.DropWatch ? $"drop watch on, {counts.DropEvents} events" : "drop watch off";
        return $"{counts.Entries} entries, {counts.Routed} routed, {counts.Domains} domains, {drops}";
    }

    private LiveCounts? Remote(string config)
    {
        var served = RuntimeSnapshotPipe.Send(config, RuntimeSnapshotPipe.OpCounts, logger);
        if (served is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LiveCounts>(served);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "runtime counts: unreadable reply for {Tunnel}", config);
            return null;
        }
    }

    private static void Add(List<CacheEntry> entries, ref int total, string kind, string key, string value)
    {
        total++;
        if (entries.Count < MaxCacheEntries)
        {
            entries.Add(new CacheEntry(kind, key, value));
        }
    }

    // Underlay the tunnel rides: plain UDP, or wstunnel with its own target. Path and credentials stay masked.
    private static string Carrier(string configText, ConfigTransport? transport)
    {
        if (transport?.UseWebSocket != true)
        {
            return "udp";
        }

        var host = WgConfigEditor.GetEndpoint(configText)?.Split(':')[0] ?? string.Empty;
        var ws = WsEndpoint.Parse(transport.WebSocketHost, transport.WebSocketPort, host);
        var token = string.IsNullOrEmpty(ws.PathPrefix) ? string.Empty : $", path {Masked}";
        var auth = string.IsNullOrEmpty(ws.Credentials) ? string.Empty : $", auth {Masked}";
        return $"websocket {ws.Host}:{ws.Port}{token}{auth}";
    }

    // The config text the engine is handed at connect: the stored file with the v6 strip, the resolved AllowedIPs,
    // the effective MTU and the injected keepalive applied. A live device supplies what the connect decides at
    // runtime - the pinned or carrier-rewritten endpoint and the prefixes actually loaded.
    private static string Effective(string configText, ConfigTransport? transport, TunnelGeo? geo, bool split, string? device)
    {
        var stripV6 = !(transport?.UseIpv6 ?? false);
        var text = configText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var live = DeviceAllowedIps(device);
        var allowed = live.Count > 0
            ? live.Select(entry => entry.Cidr).ToList()
            : PlannedAllowedIps(text, geo, split, stripV6);
        var shown = allowed.Count > MaxAllowedIps ? allowed.Take(MaxAllowedIps).ToList() : allowed;

        if (stripV6)
        {
            text = WgConfigEditor.StripIpv6Addresses(text);
        }

        text = WgConfigEditor.ApplyAllowedIps(text, shown);
        // The agent applies the resolvers on the adapter itself, so the file it hands over carries no DNS.
        text = WgConfigEditor.RemoveDns(text);
        text = WgConfigEditor.SetMtu(text, TunnelRunner.EffectiveMtu(transport?.Mtu ?? 0));
        text = WgConfigEditor.EnsurePersistentKeepalive(text, TunnelRunner.DefaultKeepaliveSeconds);

        var endpoint = DeviceEndpoint(device);
        if (endpoint is not null)
        {
            text = WgConfigEditor.SetEndpoint(text, endpoint);
        }

        return Note(allowed.Count, shown.Count) + Redact(text);
    }

    // What the next connect would load. Split advertises the resolver infrastructure and nothing else - every proxy
    // destination earns its /32 on contact, so this set is near-empty by design. Full tunnel keeps its own halves.
    private static IReadOnlyList<string> PlannedAllowedIps(string configText, TunnelGeo? geo, bool split, bool stripV6)
    {
        var routes = split ? new List<string>() : new List<string>(geo?.Routes ?? []);
        if (split)
        {
            foreach (var resolver in TunnelRunner.TunnelResolvers(TunnelRunner.ConfigResolvers(configText)))
            {
                var route = $"{resolver}/32";
                if (!routes.Contains(route))
                {
                    routes.Add(route);
                }
            }
        }

        var allowed = AllowedIpsResolver.Build(split, WgConfigEditor.GetAllowedIps(configText), routes);
        if (stripV6)
        {
            allowed = [.. allowed.Where(entry => !entry.Contains(':'))];
        }

        return TunnelRunner.SplitDefaultRoutes(allowed);
    }

    // Header comments: what the file leaves out, and whether the prefix line was cut.
    private static string Note(int total, int shown)
    {
        var dns = "# handed to the engine as-is; DNS is applied on the adapter, not through the file\n";
        return total > shown ? $"{dns}# AllowedIPs cut to {shown} of {total} entries\n" : dns;
    }

    // The engine's live view, or null when the device is unreachable.
    private string? ReadDevice(string config)
    {
        try
        {
            return uapi.Get(config);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "runtime inspector: device unreachable");
            return null;
        }
    }

    // Live device state: everything the engine reports, keys masked, one row per prefix under the peer that
    // carries it. A country rule puts thousands on one peer, so the tail past the cap is reported as a count.
    private static void AppendDevice(StringBuilder text, string state)
    {
        var allowed = 0;
        foreach (var line in state.Split('\n'))
        {
            var entry = line.Trim();
            var split = entry.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            var key = entry[..split];
            var value = entry[(split + 1)..];
            if (key == "allowed_ip")
            {
                allowed++;
                if (allowed <= MaxAllowedIps)
                {
                    Row(text, "  allowed ip", value);
                }

                continue;
            }

            if (key == "public_key")
            {
                AppendAllowedTail(text, allowed);
                allowed = 0;
                Row(text, "peer", ToBase64(value));
                continue;
            }

            Row(text, PeerKey(key), Format(key, value));
        }

        AppendAllowedTail(text, allowed);
    }

    // Closes a peer's prefix list when it ran past the cap.
    private static void AppendAllowedTail(StringBuilder text, int allowed)
    {
        if (allowed > MaxAllowedIps)
        {
            Row(text, "  allowed ips", string.Create(CultureInfo.InvariantCulture, $"{MaxAllowedIps} of {allowed} shown"));
        }
    }

    // Endpoint the live device dials: the pinned IP, or the local carrier port when wstunnel is in front.
    private static string? DeviceEndpoint(string? state)
    {
        foreach (var line in (state ?? string.Empty).Split('\n'))
        {
            var entry = line.Trim();
            if (entry.StartsWith("endpoint=", StringComparison.Ordinal))
            {
                return entry["endpoint=".Length..];
            }
        }

        return null;
    }

    // Prefixes the live device carries, tagged with the peer that owns them.
    private static IReadOnlyList<(string Peer, string Cidr)> DeviceAllowedIps(string? state)
    {
        if (state is null)
        {
            return [];
        }

        var result = new List<(string, string)>();
        var peer = "-";
        foreach (var line in state.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.StartsWith("public_key=", StringComparison.Ordinal))
            {
                peer = ToBase64(entry["public_key=".Length..]);
            }
            else if (entry.StartsWith("allowed_ip=", StringComparison.Ordinal))
            {
                result.Add((peer, entry["allowed_ip=".Length..]));
            }
        }

        return result;
    }

    // Peer fields are indented under the peer that owns them.
    private static string PeerKey(string key)
    {
        return key switch
        {
            "preshared_key" or "endpoint" or "persistent_keepalive_interval" or "last_handshake_time_sec"
                or "last_handshake_time_nsec" or "rx_bytes" or "tx_bytes" or "protocol_version" => $"  {key}",
            _ => key,
        };
    }

    private static string Format(string key, string value)
    {
        return key switch
        {
            "private_key" or "preshared_key" => Masked,
            "last_handshake_time_sec" => Handshake(value),
            "rx_bytes" or "tx_bytes" => Bytes(value),
            _ => value,
        };
    }

    private static string Handshake(string value)
    {
        if (!long.TryParse(value, out var seconds) || seconds <= 0)
        {
            return "never";
        }

        var moment = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        var age = (int)(DateTimeOffset.Now - moment).TotalSeconds;
        return $"{moment:yyyy-MM-dd HH:mm:ss} ({age} s ago)";
    }

    private static string Bytes(string value)
    {
        if (!long.TryParse(value, out var bytes))
        {
            return value;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double scaled = bytes;
        var unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{scaled:0.#} {units[unit]}");
    }

    private static string ToBase64(string hex)
    {
        try
        {
            return Convert.ToBase64String(Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return hex;
        }
    }

    // Key lines never leave the agent.
    private static string Redact(string config)
    {
        var lines = config.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new StringBuilder();
        foreach (var line in lines)
        {
            var split = line.IndexOf('=');
            var key = split > 0 ? line[..split].Trim() : string.Empty;
            var secret = key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase);
            result.Append(secret ? $"{key} = {Masked}" : line).Append('\n');
        }

        return result.ToString();
    }

    private static string Join(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "-" : string.Join(", ", values);
    }

    private static string Oneline(string? value)
    {
        var text = (value ?? string.Empty).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return text.Length == 0 ? "-" : text;
    }

    private static void Section(StringBuilder text, string title)
    {
        if (text.Length > 0)
        {
            text.Append('\n');
        }

        text.Append('[').Append(title).Append("]\n");
    }

    private static void Row(StringBuilder text, string key, string value)
    {
        text.Append(key.PadRight(KeyWidth)).Append(value).Append('\n');
    }
}
