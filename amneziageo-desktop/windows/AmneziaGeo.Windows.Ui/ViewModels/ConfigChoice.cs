namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>The kind of a <see cref="ConfigChoice"/> exposed to the profile's config combo.</summary>
internal enum ConfigChoiceKind
{
    /// <summary>A real, persisted config selected by its name.</summary>
    Real,

    /// <summary>The synthetic "no config" choice (the profile has none yet).</summary>
    None,

    /// <summary>The synthetic "+ Новая конфигурация" sentinel that reveals the inline import form.</summary>
    New,
}

/// <summary>
/// A config pick exposed to the profile's config combo box, mirroring <see cref="RoutingListChoice"/>.
/// A config's identity is its NAME (the primary key for all per-config state), so real choices carry the
/// config name; the two synthetic choices ("none" / "new") are distinguished by <see cref="Kind"/> rather
/// than by name, so a real config that happened to be named like a sentinel label is never confused for one.
/// </summary>
internal sealed record ConfigChoice(string Name, ConfigChoiceKind Kind = ConfigChoiceKind.Real)
{
    /// <summary>The synthetic "no config" choice.</summary>
    public static ConfigChoice None { get; } = new("- не задан -", ConfigChoiceKind.None);

    /// <summary>
    /// The synthetic "create a new config" choice: picking it reveals the inline new-config import form,
    /// mirroring the "+ Новый список" sentinel in the routing-list combo.
    /// </summary>
    public static ConfigChoice NewConfig { get; } = new("+ Новая конфигурация", ConfigChoiceKind.New);

    /// <summary>True for the synthetic "no config" choice.</summary>
    public bool IsNone => Kind == ConfigChoiceKind.None;

    /// <summary>True for the synthetic "+ Новая конфигурация" sentinel.</summary>
    public bool IsNewSentinel => Kind == ConfigChoiceKind.New;

    /// <summary>True for a real, persisted config (selectable / reusable across profiles).</summary>
    public bool IsReal => Kind == ConfigChoiceKind.Real;
}
