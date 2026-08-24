using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Portable bundles: exports the selected configs and routing lists, and imports them back.
/// </summary>
internal sealed class BundleCommands(IStateStore store, GeoConfigurator geo)
{
    // Export selection from OpExportBundle's arg0 JSON; all arrays optional. RoutingRules maps a routing
    // list name to the rule tokens to KEEP; an absent list keeps all its rules.
    private sealed record SelectionRequest(
        string[]? Configs,
        string[]? RoutingLists,
        Dictionary<string, string[]>? RoutingRules);

    /// <summary>
    /// Builds a bundle from the selected configs and routing lists.
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

        var configNames = new HashSet<string>(selection.Configs ?? [], StringComparer.Ordinal);
        var routingNames = new HashSet<string>(selection.RoutingLists ?? [], StringComparer.Ordinal);
        var pickedConfigs = new HashSet<string>(configNames, StringComparer.Ordinal);
        var pickedLists = new HashSet<string>(routingNames, StringComparer.Ordinal);
        var missing = new List<string>();

        if (configNames.Count == 0 && routingNames.Count == 0)
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
            configBlocks.Add(new PortableBundle.ConfigBlock(name, configText, transport, geoBlock, dns?.Servers, exclusions?.Exclusions, await RoutingNameAsync(name, ct).ConfigureAwait(false)));
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

        if (missing.Count > 0)
        {
            return new IpcAck(false, $"not found: {string.Join(", ", missing)}");
        }

        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            configBlocks,
            routingBlocks);
        return new IpcAck(true, PortableBundle.Serialize(bundle));
    }

    // Имя списка, к которому привязана конфигурация; null оставляет её на списке по умолчанию.
    private async Task<string?> RoutingNameAsync(string config, CancellationToken ct)
    {
        if (await store.GetConfigRoutingAsync(config, ct).ConfigureAwait(false) is not { } binding)
        {
            return null;
        }

        return binding.RoutingListId is { } listId
            ? (await store.GetRoutingListAsync(listId, ct).ConfigureAwait(false))?.Name
            : PortableBundle.NoRoutingList;
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

        // Configs and routing lists each own a separate name space; the snapshots detect collisions.
        var existingConfigs = new HashSet<string>(await store.ListConfigNamesAsync(ct).ConfigureAwait(false), StringComparer.Ordinal);
        var existingLists = (await store.ListRoutingListsAsync(ct).ConfigureAwait(false))
            .ToDictionary(l => l.Name, l => l, StringComparer.Ordinal);

        // Growing name spaces so the add-as-new path never reuses a name taken earlier in THIS import.
        var configNames = new HashSet<string>(existingConfigs, StringComparer.Ordinal);
        var listNames = new HashSet<string>(existingLists.Keys, StringComparer.Ordinal);

        var configNameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var routingMap = new Dictionary<string, long>(StringComparer.Ordinal);
        var renames = new List<string>();
        var importedConfigs = 0;
        var importedLists = 0;

        foreach (var block in bundle.Configs)
        {
            var incoming = SanitizeName(block.Name);

            // Same-name config and a non-default policy: act in place, keeping its bindings.
            if (existingConfigs.Contains(incoming) && policy != "new")
            {
                if (policy == "skip")
                {
                    continue;
                }

                await store.SaveConfigAsync(incoming, block.ConfigText, ct).ConfigureAwait(false);
                await ApplyConfigExtrasAsync(incoming, block, policy, ct).ConfigureAwait(false);
                configNameMap[block.Name] = incoming;
                importedConfigs++;
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
            importedConfigs++;
        }

        foreach (var block in bundle.RoutingLists)
        {
            // Same-name list and a non-default policy: act on the existing row, so the selection stays valid.
            if (existingLists.TryGetValue(block.Name, out var existingList) && policy != "new")
            {
                if (policy == "skip")
                {
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
                importedLists++;
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
            importedLists++;
        }

        // Список каждой конфигурации, когда оба пространства имён уже уложены.
        foreach (var block in bundle.Configs)
        {
            if (block.RoutingList is not { Length: > 0 } wanted || !configNameMap.TryGetValue(block.Name, out var config))
            {
                continue;
            }

            if (string.Equals(wanted, PortableBundle.NoRoutingList, StringComparison.Ordinal))
            {
                await store.SetConfigRoutingAsync(new ConfigRouting(config, null), ct).ConfigureAwait(false);
            }
            else if (routingMap.TryGetValue(wanted, out var listId))
            {
                await store.SetConfigRoutingAsync(new ConfigRouting(config, listId), ct).ConfigureAwait(false);
            }
        }

        var shown = renames.Distinct(StringComparer.Ordinal).ToList();
        if (shown.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key("Agent_BundleImported", importedConfigs, importedLists));
        }

        return shown.Count <= 5
            ? new IpcAck(true, IpcMessage.Key("Agent_BundleImportedRenamed", importedConfigs, importedLists, string.Join(", ", shown)))
            : new IpcAck(true, IpcMessage.Key("Agent_BundleImportedRenamedMany", importedConfigs, importedLists));
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
            baseName = "config";
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
