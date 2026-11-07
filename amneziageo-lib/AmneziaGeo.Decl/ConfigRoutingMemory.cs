namespace AmneziaGeo.Decl;

/// <summary>
/// Remembers routing selections in a configuration library.
/// </summary>
public sealed class ConfigRoutingMemory
{
    private Dictionary<string, long?> _choices = new(StringComparer.Ordinal);

    /// <summary>
    /// Initial selection for configurations without a saved choice.
    /// </summary>
    public long? Default { get; set; }

    /// <summary>
    /// Saved selections by configuration name.
    /// </summary>
    public Dictionary<string, long?> Choices
    {
        get => _choices;
        set => _choices = value ?? new Dictionary<string, long?>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the configuration's saved selection.
    /// </summary>
    public long? For(string? name) => name is not null && Choices.TryGetValue(name, out var choice) ? choice : Default;

    /// <summary>
    /// Returns the configuration carrying the connection.
    /// </summary>
    public static string? ConnectionTarget(string? selected, string? bound, bool active) =>
        active && !string.IsNullOrEmpty(bound) ? bound : selected;

    /// <summary>
    /// Returns the routing list used by the connection.
    /// </summary>
    public long? ForConnection(string? selected, string? bound, bool active, IReadOnlyDictionary<string, ConfigTransport> transports)
    {
        var target = ConnectionTarget(selected, bound, active);
        if (target is { Length: > 0 } && !ConfigRouting.Allowed(transports.GetValueOrDefault(target)))
        {
            return null;
        }

        return For(target);
    }

    /// <summary>
    /// Saves the configuration's selection.
    /// </summary>
    public void Remember(string? name, long? list)
    {
        if (string.IsNullOrEmpty(name))
        {
            Default = list;
        }
        else
        {
            Choices[name] = list;
        }
    }

    /// <summary>
    /// Seeds configurations with their inherited selection.
    /// </summary>
    public void Seed(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            Choices.TryAdd(name, Default);
        }
    }

    /// <summary>
    /// Moves the selection to the renamed configuration.
    /// </summary>
    public void Rename(string oldName, string newName)
    {
        if (Choices.Remove(oldName, out var choice))
        {
            Choices[newName] = choice;
        }
        else
        {
            Choices.TryAdd(newName, Default);
        }
    }

    /// <summary>
    /// Clears references to a removed routing list.
    /// </summary>
    public void RemoveList(long list)
    {
        foreach (var name in Choices.Where(entry => entry.Value == list).Select(entry => entry.Key).ToArray())
        {
            Choices[name] = null;
        }

        if (Default == list)
        {
            Default = null;
        }
    }
}
