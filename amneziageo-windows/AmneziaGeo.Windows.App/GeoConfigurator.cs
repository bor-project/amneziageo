using System.Linq;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Applies and enumerates per-config geo split-tunnel settings, shared by the agent command channel.
/// </summary>
internal sealed class GeoConfigurator(IStateStore store)
{
    /// <summary>
    /// Materializes the rule tokens and persists the geo settings for a config.
    /// </summary>
    public async Task<(int Rules, int Routes, int Domains)> ApplyAsync(string name, bool on, IReadOnlyList<string> ruleTokens, CancellationToken ct = default)
    {
        var rules = new List<GeoRule>();
        foreach (var token in ruleTokens)
        {
            var rule = ParseRule(token);
            if (rule is not null)
            {
                rules.Add(rule);
            }
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct));
        var (routes, domains, apps) = GeoMaterializer.Materialize(rules, index);
        await store.SaveTunnelGeoAsync(new TunnelGeo(name, on, rules, routes, domains, apps), ct);
        return (rules.Count, routes.Count, domains.Count);
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

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct));
        return await store.SaveRoutingListAsync(MaterializeRoutingList(listId, name, rules, index), ct);
    }

    /// <summary>
    /// Re-materializes every stored routing list against the current geo sources. Called after
    /// geo sources are added, removed, or refreshed.
    /// </summary>
    public async Task RematerializeAllRoutingListsAsync(CancellationToken ct = default)
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct));
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
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct));
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
    /// Parses a role-tagged token ("direct|geoip:ru", "block|domain:x"); a bare token defaults to the Proxy role.
    /// </summary>
    public static GeoRule? ParseRoleRule(string text)
    {
        var (role, token) = SplitRole(text);
        var rule = ParseRule(token);
        return rule is null ? null : rule with { Role = role };
    }

    /// <summary>
    /// Formats a typed rule with its role prefix ("proxy|geosite:openai").
    /// </summary>
    public static string FormatWithRole(GeoRule rule) => $"{RoleToken(rule.Role)}|{Format(rule)}";

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
}
