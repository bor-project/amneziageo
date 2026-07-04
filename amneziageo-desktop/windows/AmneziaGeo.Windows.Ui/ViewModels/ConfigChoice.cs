using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The kind of a ConfigChoice exposed to the profile's config combo.
/// </summary>
internal enum ConfigChoiceKind
{
    /// <summary>
    /// A real, persisted config selected by its name.
    /// </summary>
    Real,

    /// <summary>
    /// The synthetic "no config" choice (the profile has none yet).
    /// </summary>
    None,
}

/// <summary>
/// A config pick for the profile's config combo box. A config's identity is its name; the synthetic "none" choice is distinguished by Kind.
/// </summary>
internal sealed record ConfigChoice(string Name, ConfigChoiceKind Kind = ConfigChoiceKind.Real)
{
    /// <summary>
    /// The synthetic "no config" choice.
    /// </summary>
    public static ConfigChoice None { get; } = new(Loc.Instance.Get("ConfigChoice_NoneLabel"), ConfigChoiceKind.None);

    /// <summary>
    /// True for the synthetic "no config" choice.
    /// </summary>
    public bool IsNone => Kind == ConfigChoiceKind.None;

    /// <summary>
    /// True for a real, persisted config (selectable / reusable across profiles).
    /// </summary>
    public bool IsReal => Kind == ConfigChoiceKind.Real;
}
