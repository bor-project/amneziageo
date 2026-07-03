using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>The kind of a <see cref="ConfigChoice"/> exposed to the profile's config combo.</summary>
internal enum ConfigChoiceKind
{
    /// <summary>A real, persisted config selected by its name.</summary>
    Real,

    /// <summary>The synthetic "no config" choice (the profile has none yet).</summary>
    None,
}

/// <summary>
/// A config pick exposed to the profile's config combo box, mirroring <see cref="RoutingListChoice"/>.
/// A config's identity is its NAME (the primary key for all per-config state), so real choices carry the
/// config name; the synthetic "none" choice is distinguished by <see cref="Kind"/> rather than by name, so a
/// real config that happened to be named like the sentinel label is never confused for one. Creating a config
/// is a button («+ Новая конфигурация») in the Config section, no longer a synthetic combo entry (#111).
/// </summary>
internal sealed record ConfigChoice(string Name, ConfigChoiceKind Kind = ConfigChoiceKind.Real)
{
    /// <summary>The synthetic "no config" choice.</summary>
    public static ConfigChoice None { get; } = new(Loc.Instance.Get("ConfigChoice_NoneLabel"), ConfigChoiceKind.None);

    /// <summary>True for the synthetic "no config" choice.</summary>
    public bool IsNone => Kind == ConfigChoiceKind.None;

    /// <summary>True for a real, persisted config (selectable / reusable across profiles).</summary>
    public bool IsReal => Kind == ConfigChoiceKind.Real;
}
