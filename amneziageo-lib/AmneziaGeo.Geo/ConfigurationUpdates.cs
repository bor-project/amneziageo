using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

public static class ConfigurationUpdates
{

    /// <summary>
    /// Returns the pending configuration update.
    /// </summary>
    public static async Task<ConfigurationUpdate?> ReadAsync(IStateStore store, string name, CancellationToken ct)
    {
        var raw = await store.GetSettingAsync(ConfigurationUpdate.Key(name), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        try
        {
            var update = JsonSerializer.Deserialize<ConfigurationUpdate>(raw);
            return update is { Revision.Length: 64, BaseRevision.Length: 64, Fields: not null }
                && update.Revision.All(char.IsAsciiHexDigit) && update.BaseRevision.All(char.IsAsciiHexDigit) ? update : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the available configuration.
    /// </summary>
    public static Task SaveAsync(IStateStore store, string name, string current, string offered, CancellationToken ct)
    {
        var update = SubscriptionMerge.SameText(current, offered) ? null
            : new ConfigurationUpdate(ConfigurationRevision.Of(offered), ConfigurationRevision.Of(current), ConfigurationRevision.Fields(current, offered));
        return store.SetSettingAsync(ConfigurationUpdate.Key(name), update is null ? string.Empty : JsonSerializer.Serialize(update), ct);
    }

    /// <summary>
    /// Removes the pending configuration update.
    /// </summary>
    public static Task ClearAsync(IStateStore store, string name, CancellationToken ct) => store.SetSettingAsync(ConfigurationUpdate.Key(name), string.Empty, ct);

    /// <summary>
    /// Attaches pending updates to configuration entries.
    /// </summary>
    public static async Task<IReadOnlyList<ConfigEntry>> DescribeAsync(IStateStore store, IReadOnlyList<ConfigEntry> entries, CancellationToken ct)
    {
        var described = new List<ConfigEntry>(entries.Count);
        foreach (var entry in entries)
        {
            described.Add(entry with { Update = await ReadAsync(store, entry.Name, ct).ConfigureAwait(false) });
        }
        return described;
    }
}
