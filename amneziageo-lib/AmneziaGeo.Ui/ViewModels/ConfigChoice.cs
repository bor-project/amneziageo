using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// The kind of a ConfigChoice exposed to the home config combo.
/// </summary>
internal enum ConfigChoiceKind
{
    /// <summary>
    /// A real, persisted config selected by its name.
    /// </summary>
    Real,

    /// <summary>
    /// The synthetic "no config" choice (nothing selected yet).
    /// </summary>
    None,
}

/// <summary>
/// A config pick for the home config combo box. A config's identity is its name; the synthetic "none" choice is distinguished by Kind.
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
    /// True for a real, persisted config.
    /// </summary>
    public bool IsReal => Kind == ConfigChoiceKind.Real;
}
