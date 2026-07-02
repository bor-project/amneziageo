namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>The kind of a <see cref="ProfileChoice"/> exposed to a profile combo box.</summary>
internal enum ProfileChoiceKind
{
    /// <summary>A real, saved profile selected by its name.</summary>
    Real,

    /// <summary>The synthetic "no profile" choice (nothing selected yet).</summary>
    None,

    /// <summary>The synthetic "+ Новый профиль" sentinel that creates a profile and opens its editor.</summary>
    New,
}

/// <summary>
/// A profile pick exposed to the main-window and Profile-section combo boxes, mirroring
/// <see cref="ConfigChoice"/> / <see cref="RoutingListChoice"/>. A profile's identity is its NAME, so real
/// choices carry the profile name; the two synthetic choices ("none" / "new") are distinguished by
/// <see cref="Kind"/> rather than by name, so a real profile named like a sentinel label is never confused
/// for one. The host resolves a real choice to its <c>BalancerItemViewModel</c> by name.
/// </summary>
internal sealed record ProfileChoice(string Name, ProfileChoiceKind Kind = ProfileChoiceKind.Real)
{
    /// <summary>The synthetic "no profile" choice (shown, and selectable, when nothing is picked).</summary>
    public static ProfileChoice None { get; } = new("— не выбрано —", ProfileChoiceKind.None);

    /// <summary>
    /// The synthetic "create a new profile" choice: picking it creates a profile and opens it in the
    /// Profile section editor (redirecting there from the home combo).
    /// </summary>
    public static ProfileChoice New { get; } = new("+ Новый профиль", ProfileChoiceKind.New);

    /// <summary>True for the synthetic "no profile" choice.</summary>
    public bool IsNone => Kind == ProfileChoiceKind.None;

    /// <summary>True for the synthetic "+ Новый профиль" sentinel.</summary>
    public bool IsNew => Kind == ProfileChoiceKind.New;

    /// <summary>True for a real, saved profile.</summary>
    public bool IsReal => Kind == ProfileChoiceKind.Real;
}
