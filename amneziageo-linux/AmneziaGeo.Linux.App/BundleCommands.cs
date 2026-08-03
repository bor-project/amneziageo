using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Portable bundles: exports the selected configs, routing lists and profiles, and imports them back.
/// </summary>
internal sealed class BundleCommands(IStateStore store, GeoConfigurator geo)
{
    // Export selection from OpExportBundle's arg0 JSON; all arrays optional. RoutingRules maps a routing
    // list name to the rule tokens to KEEP; an absent list keeps all its rules.
    private sealed record SelectionRequest(
        string[]? Profiles,
        string[]? Configs,
        string[]? RoutingLists,
        Dictionary<string, string[]>? RoutingRules);

    /// <summary>
    /// Builds a bundle from the selected configs, routing lists and profiles.
    /// </summary>
    public async Task<IpcAck> ExportAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "export-bundle requires a selection json");
        }

        var selection = ParseSelection(args[0]);
        if (selection is null)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_ExportSelectionParseFailed"));
        }

        // Picking a profile carries the config and routing list it binds, so the bundle can reconnect as is.
        var configNames = new HashSet<string>(selection.Configs ?? [], StringComparer.Ordinal);
        var routingNames = new HashSet<string>(selection.RoutingLists ?? [], StringComparer.Ordinal);
        var profileNames = new HashSet<string>(selection.Profiles ?? [], StringComparer.Ordinal);
        var pickedConfigs = new HashSet<string>(configNames, StringComparer.Ordinal);
        var pickedLists = new HashSet<string>(routingNames, StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var profileName in profileNames)
        {
            var profile = await store.GetProfileAsync(profileName, ct).ConfigureAwait(false);
            if (profile is null)
            {
                missing.Add(profileName);
                continue;
            }

            if (!string.IsNullOrEmpty(profile.Config))
            {
                configNames.Add(profile.Config);
            }

            var (listId, _) = await store.GetProfileRoutingAsync(profileName, ct).ConfigureAwait(false);
            if (listId is not null && await store.GetRoutingListAsync(listId.Value, ct).ConfigureAwait(false) is { } boundList)
            {
                routingNames.Add(boundList.Name);
            }
        }

        if (configNames.Count == 0 && routingNames.Count == 0 && profileNames.Count == 0)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NothingSelectedForExport"));
        }

        var configBlocks = new List<PortableBundle.ConfigBlock>();
        foreach (var name in configNames)
        {
            var configText = await store.GetConfigTextAsync(name, ct).ConfigureAwait(false);
            if (configText is null)
            {
                if (pickedConfigs.Contains(name))
                {
                    missing.Add(name);
                }

                continue;
            }

            var transport = await store.GetConfigTransportAsync(name, ct).ConfigureAwait(false) is { } tr
                ? new PortableBundle.TransportBlock(tr.UseWebSocket, tr.WebSocketHost, tr.WebSocketPort, tr.Mtu, tr.UseIpv6)
                : null;

            var ownGeo = await store.GetTunnelGeoAsync(name, ct).ConfigureAwait(false);
            var geoBlock = ownGeo is not null && (ownGeo.GeoSplit || ownGeo.Rules.Count > 0)
                ? new PortableBundle.GeoBlock(ownGeo.GeoSplit, [.. ownGeo.Rules.Select(GeoConfigurator.Format)])
                : null;

            var dns = await store.GetConfigDnsAsync(name, ct).ConfigureAwait(false);
            var exclusions = await store.GetConfigExclusionsAsync(name, ct).ConfigureAwait(false);
            configBlocks.Add(new PortableBundle.ConfigBlock(name, configText, transport, geoBlock, dns?.Servers, exclusions?.Exclusions));
        }

        var routingBlocks = new List<PortableBundle.RoutingBlock>();
        if (routingNames.Count > 0)
        {
            var allLists = await store.ListRoutingListsAsync(ct).ConfigureAwait(false);
            foreach (var name in routingNames)
            {
                var list = allLists.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
                if (list is null)
                {
                    if (pickedLists.Contains(name))
                    {
                        missing.Add(name);
                    }

                    continue;
                }

                // Role-tagged: bare tokens would re-import every rule as Proxy and drop its bucket.
                var rules = list.Rules.Select(GeoConfigurator.FormatWithRole).ToList();

                // Rules the user unchecked in the export tree are dropped; no entry keeps the whole list.
                if (selection.RoutingRules is not null && selection.RoutingRules.TryGetValue(name, out var kept))
                {
                    var keepSet = new HashSet<string>(kept, StringComparer.Ordinal);
                    rules = [.. rules.Where(keepSet.Contains)];
                }

                var settingsBlock = await store.GetRoutingSettingsAsync(list.Id, ct).ConfigureAwait(false) is { } settings
                    ? new PortableBundle.RoutingSettingsBlock(settings.Exclusions, settings.AllUdp, settings.Mode, settings.UseGlobalProxy)
                    : null;

                routingBlocks.Add(new PortableBundle.RoutingBlock(name, rules, settingsBlock));
            }
        }

        var profileBlocks = new List<PortableBundle.ProfileBlock>();
        foreach (var name in profileNames)
        {
            var profile = await store.GetProfileAsync(name, ct).ConfigureAwait(false);
            if (profile is null)
            {
                continue;
            }

            var (listId, useRouting) = await store.GetProfileRoutingAsync(name, ct).ConfigureAwait(false);
            var routingListName = listId is not null && await store.GetRoutingListAsync(listId.Value, ct).ConfigureAwait(false) is { } list
                ? list.Name
                : null;

            profileBlocks.Add(new PortableBundle.ProfileBlock(name, profile.Config.Length > 0 ? profile.Config : null, routingListName, useRouting));
        }

        if (missing.Count > 0)
        {
            return new IpcAck(false, $"not found: {string.Join(", ", missing)}");
        }

        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            configBlocks,
            routingBlocks,
            profileBlocks);
        return new IpcAck(true, PortableBundle.Serialize(bundle));
    }

    /// <summary>
    /// Writes a bundle into the library. The policy decides what a name already taken means:
    /// new (numbered copy), replace, skip, or merge.
    /// </summary>
    public async Task<IpcAck> ImportAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "import-bundle requires the bundle json");
        }

        var policy = args.Count > 1 ? args[1] : "new";

        PortableBundle.Bundle? bundle;
        try
        {
            bundle = PortableBundle.Deserialize(args[0]);
        }
        catch (JsonException ex)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_BundleParseFailed", ex.Message));
        }

        if (bundle is null || !string.Equals(bundle.Format, PortableBundle.FormatTag, StringComparison.Ordinal))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NotAnAmneziaGeoFile"));
        }

        if (bundle.Version > PortableBundle.CurrentVersion)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_BundleTooNew", bundle.Version));
        }

        // Configs, profiles and routing lists each own a separate name space; the snapshots detect collisions.
        var existingConfigs = new HashSet<string>(await store.ListConfigNamesAsync(ct).ConfigureAwait(false), StringComparer.Ordinal);
        var existingProfiles = new HashSet<string>(await store.ListProfileNamesAsync(ct).ConfigureAwait(false), StringComparer.Ordinal);
        var existingLists = (await store.ListRoutingListsAsync(ct).ConfigureAwait(false))
            .ToDictionary(l => l.Name, l => l, StringComparer.Ordinal);

        // Growing name spaces so the add-as-new path never reuses a name taken earlier in THIS import.
        var configNames = new HashSet<string>(existingConfigs, StringComparer.Ordinal);
        var profileNames = new HashSet<string>(existingProfiles, StringComparer.Ordinal);
        var listNames = new HashSet<string>(existingLists.Keys, StringComparer.Ordinal);

        var configNameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var routingMap = new Dictionary<string, long>(StringComparer.Ordinal);
        var renames = new List<string>();

        foreach (var block in bundle.Configs)
        {
            var incoming = SanitizeName(block.Name);

            // Same-name config and a non-default policy: act in place, keeping its bindings.
            if (existingConfigs.Contains(incoming) && policy != "new")
            {
                if (policy == "skip")
                {
                    configNameMap[block.Name] = incoming;
                    continue;
                }

                await store.SaveConfigAsync(incoming, block.ConfigText, ct).ConfigureAwait(false);
                await ApplyConfigExtrasAsync(incoming, block, policy, ct).ConfigureAwait(false);
                configNameMap[block.Name] = incoming;
                continue;
            }

            var freeName = FreeName(incoming, configNames);
            configNames.Add(freeName);
            if (!string.Equals(freeName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{freeName}»");
            }

            await store.SaveConfigAsync(freeName, block.ConfigText, ct).ConfigureAwait(false);
            await ApplyConfigExtrasAsync(freeName, block, "new", ct).ConfigureAwait(false);
            configNameMap[block.Name] = freeName;
        }

        foreach (var block in bundle.RoutingLists)
        {
            // Same-name list and a non-default policy: act on the existing row, so bound profiles stay bound.
            if (existingLists.TryGetValue(block.Name, out var existingList) && policy != "new")
            {
                if (policy == "skip")
                {
                    routingMap[block.Name] = existingList.Id;
                    continue;
                }

                // A pre-role bundle carries bare tokens; those import as Proxy, as they did before roles existed.
                var merged = policy == "merge"
                    ? existingList.Rules.Select(GeoConfigurator.FormatWithRole).Concat(block.Rules).Distinct(StringComparer.Ordinal).ToList()
                    : block.Rules.ToList();
                await geo.ApplyToRoutingListAsync(existingList.Id, existingList.Name, merged, ct).ConfigureAwait(false);
                if (block.Settings is { } existingSettings)
                {
                    await store.SetRoutingSettingsAsync(new RoutingSettings(existingList.Id, existingSettings.Exclusions, existingSettings.AllUdp, existingSettings.Mode, existingSettings.UseGlobalProxy), ct).ConfigureAwait(false);
                }

                routingMap[block.Name] = existingList.Id;
                continue;
            }

            var freeName = FreeName(block.Name, listNames);
            listNames.Add(freeName);
            if (!string.Equals(freeName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{freeName}»");
            }

            var newId = await geo.ApplyToRoutingListAsync(0, freeName, block.Rules, ct).ConfigureAwait(false);
            if (block.Settings is { } settings)
            {
                await store.SetRoutingSettingsAsync(new RoutingSettings(newId, settings.Exclusions, settings.AllUdp, settings.Mode, settings.UseGlobalProxy), ct).ConfigureAwait(false);
            }

            routingMap[block.Name] = newId;
        }

        var importedProfiles = 0;
        foreach (var block in bundle.Profiles)
        {
            var config = ResolveConfig(block.Config, configNameMap, configNames);
            long? routingId = block.RoutingList is not null && routingMap.TryGetValue(block.RoutingList, out var listId) ? listId : null;

            var name = existingProfiles.Contains(block.Name) && policy != "new"
                ? block.Name
                : FreeName(block.Name, profileNames);
            if (policy == "skip" && existingProfiles.Contains(block.Name))
            {
                continue;
            }

            importedProfiles++;

            profileNames.Add(name);
            if (!string.Equals(name, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{name}»");
            }

            await store.SaveProfileAsync(new Profile(name, config), ct).ConfigureAwait(false);

            // No auto-target here: a bulk import must not steal the selection.
            if (routingId is not null)
            {
                await store.SetProfileRoutingAsync(name, routingId.Value, block.UseRouting, ct).ConfigureAwait(false);
            }
        }

        if (renames.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key("Agent_BundleImported", bundle.Configs.Count, bundle.RoutingLists.Count, importedProfiles));
        }

        return renames.Count <= 5
            ? new IpcAck(true, IpcMessage.Key("Agent_BundleImportedRenamed", bundle.Configs.Count, bundle.RoutingLists.Count, importedProfiles, string.Join(", ", renames)))
            : new IpcAck(true, IpcMessage.Key("Agent_BundleImportedRenamedMany", bundle.Configs.Count, bundle.RoutingLists.Count, importedProfiles));
    }

    // Settings that travel with a config; merge keeps the geo rules already stored.
    private async Task ApplyConfigExtrasAsync(string name, PortableBundle.ConfigBlock block, string policy, CancellationToken ct)
    {
        if (block.Transport is { } transport)
        {
            await store.SetConfigTransportAsync(new ConfigTransport(name, transport.UseWebSocket, transport.Host, transport.Port, transport.Mtu, transport.UseIpv6), ct).ConfigureAwait(false);
        }

        if (block.Dns is { } dns)
        {
            if (dns.Trim().Length == 0)
            {
                await store.RemoveConfigDnsAsync(name, ct).ConfigureAwait(false);
            }
            else
            {
                await store.SetConfigDnsAsync(new ConfigDns(name, dns), ct).ConfigureAwait(false);
            }
        }

        if (block.Exclusions is { } exclusions)
        {
            if (exclusions.Trim().Length == 0)
            {
                await store.RemoveConfigExclusionsAsync(name, ct).ConfigureAwait(false);
            }
            else
            {
                await store.SetConfigExclusionsAsync(new ConfigExclusions(name, exclusions), ct).ConfigureAwait(false);
            }
        }

        if (block.Geo is not { } geoBlock)
        {
            return;
        }

        var rules = geoBlock.Rules;
        if (policy == "merge")
        {
            var own = await store.GetTunnelGeoAsync(name, ct).ConfigureAwait(false);
            var keep = own?.Rules.Select(GeoConfigurator.Format) ?? [];
            rules = [.. keep.Concat(geoBlock.Rules).Distinct(StringComparer.Ordinal)];
        }

        // Rule tokens re-materialize against the local geo data.
        await geo.ApplyAsync(name, geoBlock.Split, rules, ct).ConfigureAwait(false);
    }

    // The name the bundle's config landed under, or a config already here under that name.
    private static string ResolveConfig(string? wanted, Dictionary<string, string> map, HashSet<string> present)
    {
        if (wanted is null)
        {
            return string.Empty;
        }

        if (map.TryGetValue(wanted, out var mapped))
        {
            return mapped;
        }

        return present.Contains(wanted) ? wanted : string.Empty;
    }

    private static SelectionRequest? ParseSelection(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SelectionRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Returns the desired name if free, otherwise appends " (2)", " (3)", … until one is not taken.
    private static string FreeName(string desired, HashSet<string> taken)
    {
        var baseName = desired.Trim();
        if (baseName.Length == 0)
        {
            baseName = "Профиль";
        }

        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; i < 10000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName} ({Guid.NewGuid():N})";
    }

    // Keeps imported names usable as file names on the other heads.
    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)]).Trim();
        return clean.Length == 0 ? "config" : clean;
    }
}
