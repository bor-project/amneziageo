using AmneziaGeo.Decl;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Geo;

/// <summary>
/// Applies and enumerates per-config geo split-tunnel settings, shared by the agent command channel.
/// </summary>
public sealed class GeoConfigurator(IStateStore store, IGeoFileStore files)
{
    /// <summary>
    /// Materializes the rule tokens and persists the geo settings for a config. Counts the tokens it cannot parse.
    /// </summary>
    public async Task<(int Rules, int Routes, int Domains, int Skipped)> ApplyAsync(string name, bool on, IReadOnlyList<string> ruleTokens, CancellationToken ct = default)
    {
        var rules = new List<GeoRule>();
        var skipped = 0;
        foreach (var token in ruleTokens)
        {
            var rule = ParseRule(token);
            if (rule is null)
            {
                skipped++;
                continue;
            }

            rules.Add(rule);
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct), files);
        var (routes, domains, apps) = GeoMaterializer.Materialize(rules, index);
        await store.SaveTunnelGeoAsync(new TunnelGeo(name, on, rules, routes, domains, apps), ct);
        return (rules.Count, routes.Count, domains.Count, skipped);
    }

    /// <summary>
    /// Materializes the role-tagged rule tokens and saves them as a shared routing list. When listId is 0
    /// a new list is created; otherwise the list with that id is replaced. Returns the row id.
    /// </summary>
    public async Task<long> ApplyToRoutingListAsync(long listId, string name, IReadOnlyList<string> ruleTokens, CancellationToken ct = default)
    {
        var rules = new List<GeoRule>();
        foreach (var token in ruleTokens)
        {
            var rule = ParseRoleRule(token);
            if (rule is not null)
            {
                rules.Add(rule);
            }
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct), files);
        return await store.SaveRoutingListAsync(MaterializeRoutingList(listId, name, rules, index), ct);
    }

    /// <summary>
    /// Expands the role-tagged rule tokens without storing anything, so a draft can be measured before it is saved.
    /// </summary>
    public async Task<RoutingList> MaterializeDraftAsync(IReadOnlyList<string> ruleTokens, CancellationToken ct = default)
    {
        var rules = new List<GeoRule>();
        foreach (var token in ruleTokens)
        {
            var rule = ParseRoleRule(token);
            if (rule is not null)
            {
                rules.Add(rule);
            }
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct), files);
        return MaterializeRoutingList(0, string.Empty, rules, index);
    }

    /// <summary>
    /// Materializes a plan of the list: every server that is up gets the ranges of the rules resolved onto it, and
    /// the blocking bucket gains the rules that drop their traffic while the server they name is down.
    /// </summary>
    public async Task<FleetProjection> ProjectAsync(RoutingList list, RoutingPlan plan, CancellationToken ct = default)
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct), files);
        var servers = new List<ServerProjection>(plan.Servers.Count);
        foreach (var entry in plan.Servers)
        {
            var (routes, domains, apps) = GeoMaterializer.Materialize(entry.Rules, index);
            servers.Add(new ServerProjection(entry.Server, routes, domains, apps));
        }

        var blocked = GeoMaterializer.Materialize(plan.Blocked, index);
        return new FleetProjection(
            servers,
            [.. list.BlockRoutes, .. blocked.Routes],
            [.. list.BlockDomains, .. blocked.Domains],
            plan);
    }

    // Bumped whenever a rule token starts covering something else, so stored lists are rebuilt against the new
    // expansion instead of keeping what an older version wrote.
    private const string MaterializerVersion = "2";

    private const string MaterializerVersionKey = "geo-materializer";

    /// <summary>
    /// Rebuilds every stored list when the expansion changed since they were materialized. Returns whether
    /// anything was rebuilt; a run with no readable geo database is skipped, or the lists would come back empty.
    /// </summary>
    public async Task<bool> RematerializeIfStaleAsync(CancellationToken ct = default)
    {
        var stored = await store.GetSettingAsync(MaterializerVersionKey, ct).ConfigureAwait(false);
        if (string.Equals(stored, MaterializerVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var sources = await store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        if (!sources.Any(HasFile))
        {
            return false;
        }

        await RematerializeAllRoutingListsAsync(ct).ConfigureAwait(false);
        await store.SetSettingAsync(MaterializerVersionKey, MaterializerVersion, ct).ConfigureAwait(false);
        return true;
    }

    private bool HasFile(GeoSource source)
    {
        using var stream = files.OpenRead(source.Name);
        return stream is not null;
    }

    /// <summary>
    /// Re-materializes every stored routing list against the current geo sources. Called after
    /// geo sources are added, removed, or refreshed.
    /// </summary>
    public async Task RematerializeAllRoutingListsAsync(CancellationToken ct = default)
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct), files);
        var lists = await store.ListRoutingListsAsync(ct);
        foreach (var list in lists)
        {
            await store.SaveRoutingListAsync(MaterializeRoutingList(list.Id, list.Name, list.Rules, index), ct);
        }
    }

    // Materializes a list's rules into the role buckets. Apps stay Proxy-only (per-app tunneling).
    private static RoutingList MaterializeRoutingList(long id, string name, IReadOnlyList<GeoRule> rules, GeoIndex index)
    {
        var proxy = GeoMaterializer.Materialize(rules.Where(r => r.Role == RouteRole.Proxy).ToList(), index);
        var direct = GeoMaterializer.Materialize(rules.Where(r => r.Role == RouteRole.Direct).ToList(), index);
        var block = GeoMaterializer.Materialize(rules.Where(r => r.Role == RouteRole.Block).ToList(), index);
        return new RoutingList(id, name, rules, proxy.Routes, proxy.Domains, proxy.Apps,
            direct.Routes, direct.Domains, block.Routes, block.Domains);
    }

    /// <summary>
    /// Returns the available geo categories as prefixed rule tokens (geosite:* and geoip:*).
    /// </summary>
    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default)
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct), files);
        var tokens = new List<string>();
        foreach (var category in index.Categories())
        {
            tokens.Add($"geosite:{category.ToLowerInvariant()}");
        }

        foreach (var country in index.Countries())
        {
            tokens.Add($"geoip:{country.ToLowerInvariant()}");
        }

        return tokens;
    }

    /// <summary>
    /// Returns every entry a geosite / geoip rule expands to. Any other rule kind carries its own value and has
    /// nothing to expand.
    /// </summary>
    public async Task<IReadOnlyList<string>> EntriesAsync(string token, CancellationToken ct = default)
    {
        var rule = ParseRule(token);
        if (rule is null || rule.Kind is not (GeoRuleKind.GeoSite or GeoRuleKind.GeoIp))
        {
            return [];
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct), files);
        var routes = new List<string>();
        var domains = new List<GeoDomain>();
        GeoMaterializer.Expand(StripPrefix(rule.Value), index, routes, domains);
        routes.AddRange(domains.Select(FormatDomain));
        return routes;
    }

    /// <summary>
    /// Renders a geosite entry in v2ray rule notation: a suffix match is bare, other kinds carry their prefix.
    /// </summary>
    public static string FormatDomain(GeoDomain domain) => domain.Kind switch
    {
        GeoDomainKind.Full => $"full:{domain.Value}",
        GeoDomainKind.Regex => $"regexp:{domain.Value}",
        GeoDomainKind.Plain => $"keyword:{domain.Value}",
        _ => domain.Value,
    };

    // Drops a repeated "geosite:" / "geoip:" prefix left inside a rule value.
    private static string StripPrefix(string value)
    {
        var colon = value.IndexOf(':');
        return colon >= 0 ? value[(colon + 1)..] : value;
    }

    /// <summary>
    /// Parses a rule token like "geosite:openai" or "domain:example.com" into a typed rule.
    /// </summary>
    public static GeoRule? ParseRule(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var value = text[(colon + 1)..];
        var kind = text[..colon].ToLowerInvariant() switch
        {
            "geosite" => GeoRuleKind.GeoSite,
            "geoip" => GeoRuleKind.GeoIp,
            "domain" => GeoRuleKind.Domain,
            "cidr" => GeoRuleKind.Cidr,
            "app" => GeoRuleKind.App,
            _ => (GeoRuleKind?)null,
        };
        return kind is null ? null : new GeoRule(kind.Value, value);
    }

    /// <summary>
    /// Formats a typed rule back into its prefixed token form.
    /// </summary>
    public static string Format(GeoRule rule)
    {
        var prefix = rule.Kind switch
        {
            GeoRuleKind.GeoSite => "geosite",
            GeoRuleKind.GeoIp => "geoip",
            GeoRuleKind.Domain => "domain",
            GeoRuleKind.App => "app",
            _ => "cidr",
        };
        return $"{prefix}:{rule.Value}";
    }

    /// <summary>
    /// Parses a role-tagged token ("direct|geoip:ru", "proxy|geoip:x|server=de|fallback=block"); a bare token
    /// defaults to the Proxy role and to whichever server carries the default route.
    /// </summary>
    public static GeoRule? ParseRoleRule(string text)
    {
        var (role, tail) = SplitRole(text);
        var (token, serverMode, server, fallbackMode, fallback) = SplitServer(tail);
        var rule = ParseRule(token);
        return rule is null
            ? null
            : (rule with
            {
                Role = role,
                ServerMode = serverMode,
                Server = server,
                FallbackMode = fallbackMode,
                Fallback = fallback,
            }).Normalized();
    }

    /// <summary>
    /// Formats a typed rule with its role prefix and the servers it names ("proxy|geosite:openai|server=de").
    /// </summary>
    public static string FormatWithRole(GeoRule rule)
    {
        var normalized = rule.Normalized();
        var server = FormatField("server", normalized.ServerMode, normalized.Server);
        var fallback = FormatField("fallback", normalized.FallbackMode, normalized.Fallback);
        return $"{FormatPortable(normalized)}{server}{fallback}";
    }

    /// <summary>
    /// Formats a typed rule without the server names, which mean nothing on another machine.
    /// </summary>
    public static string FormatPortable(GeoRule rule) => $"{RoleToken(rule.Role)}|{Format(rule)}";

    /// <summary>
    /// Merges incoming tokens into stored rules, keeping the stored rule wherever both name the same match.
    /// </summary>
    public static List<string> MergeRules(IEnumerable<GeoRule> stored, IEnumerable<string> incoming)
    {
        var rules = stored.ToList();
        var merged = rules.Select(FormatWithRole).ToList();
        var seen = rules.Select(FormatPortable).ToHashSet(StringComparer.Ordinal);
        foreach (var token in incoming)
        {
            var key = ParseRoleRule(token) is { } rule ? FormatPortable(rule) : token;
            if (seen.Add(key))
            {
                merged.Add(token);
            }
        }

        return merged;
    }

    private static string RoleToken(RouteRole role) => role switch
    {
        RouteRole.Direct => "direct",
        RouteRole.Block => "block",
        _ => "proxy",
    };

    // Splits an optional "<role>|" prefix off a token; an unknown/absent prefix means the whole text is Proxy.
    private static (RouteRole Role, string Token) SplitRole(string text)
    {
        var bar = text.IndexOf('|');
        if (bar > 0)
        {
            var role = text[..bar].ToLowerInvariant() switch
            {
                "proxy" => RouteRole.Proxy,
                "direct" => RouteRole.Direct,
                "block" => RouteRole.Block,
                "exclude" => RouteRole.Direct,
                _ => (RouteRole?)null,
            };
            if (role is not null)
            {
                return (role.Value, text[(bar + 1)..]);
            }
        }

        return (RouteRole.Proxy, text);
    }

    // Auto is the default and stays out of the token: a rule that addresses no server reads byte for byte as it
    // did before the field existed.
    private static string FormatField(string field, RuleTargetMode mode, string name) => mode switch
    {
        RuleTargetMode.Best => $"|{field}=best",
        RuleTargetMode.Server => $"|{field}={name}",
        RuleTargetMode.Direct => $"|{field}=direct",
        RuleTargetMode.Block => $"|{field}=block",
        _ => string.Empty,
    };

    // Splits the "|server=…|fallback=…" tail off a token; a configuration name carrying a bar does not survive it.
    private static (string Token, RuleTargetMode ServerMode, string Server, RuleTargetMode FallbackMode, string Fallback) SplitServer(string text)
    {
        var parts = text.Split('|');
        var server = (Mode: RuleTargetMode.Auto, Name: string.Empty);
        var fallback = (Mode: RuleTargetMode.Auto, Name: string.Empty);
        foreach (var part in parts.Skip(1))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var value = part[(separator + 1)..].Trim();
            switch (part[..separator].Trim().ToLowerInvariant())
            {
                case "server":
                    server = ParseField(value);
                    break;

                case "fallback":
                    fallback = ParseField(value);
                    break;
            }
        }

        return (parts[0], server.Mode, server.Name, fallback.Mode, fallback.Name);
    }

    // Anything but a keyword is a configuration name; "none" is how the blocking fallback used to be spelled. A
    // configuration named after a keyword loses the round trip, as does one carrying a bar.
    private static (RuleTargetMode Mode, string Name) ParseField(string value) => value.ToLowerInvariant() switch
    {
        "" or "auto" => (RuleTargetMode.Auto, string.Empty),
        "best" => (RuleTargetMode.Best, string.Empty),
        "direct" => (RuleTargetMode.Direct, string.Empty),
        "block" or "none" => (RuleTargetMode.Block, string.Empty),
        _ => (RuleTargetMode.Server, value),
    };
}
