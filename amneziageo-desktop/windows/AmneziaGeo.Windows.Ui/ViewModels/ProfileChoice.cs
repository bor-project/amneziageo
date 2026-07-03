using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>The kind of a <see cref="ProfileChoice"/> exposed to a profile combo box.</summary>
internal enum ProfileChoiceKind
{
    /// <summary>A real, saved profile selected by its name.</summary>
    Real,

    /// <summary>The synthetic "no profile" choice (nothing selected yet).</summary>
    None,
}

/// <summary>
/// A profile pick exposed to the main-window and Profile-section combo boxes, mirroring
/// <see cref="ConfigChoice"/> / <see cref="RoutingListChoice"/>. A profile's identity is its NAME, carried by
/// <see cref="Identity"/> and kept stable across an in-place rename; the displayed <see cref="Name"/> is
/// observable so the Profile section's combo can preview a name being typed in the editor live (#110) before
/// it is saved. The synthetic "none" choice is distinguished by <see cref="Kind"/> rather than by name, so a
/// real profile named like the sentinel label is never confused for one. Creating a profile is a button
/// («+ Профиль»), no longer a synthetic combo entry (#111). The host resolves a real choice to its
/// <c>BalancerItemViewModel</c> by <see cref="Identity"/>.
/// </summary>
internal sealed partial class ProfileChoice : ObservableObject
{
    public ProfileChoice(string identity, ProfileChoiceKind kind = ProfileChoiceKind.Real)
    {
        Identity = identity;
        Kind = kind;
        _name = identity;
    }

    /// <summary>The stable key: the real profile name for a real choice, unchanged by a live-typed rename.</summary>
    public string Identity { get; }

    /// <summary>Whether this is a real profile or the synthetic "none" choice.</summary>
    public ProfileChoiceKind Kind { get; }

    /// <summary>
    /// The label shown in the combo. For a real choice it tracks the Profile editor's name field live while a
    /// rename is being typed (#110); it snaps back to the persisted name once the rename is saved (or dropped).
    /// </summary>
    [ObservableProperty]
    private string _name;

    /// <summary>The synthetic "no profile" choice (shown, and selectable, when nothing is picked).</summary>
    public static ProfileChoice None { get; } = new(Loc.Instance.Get("ProfileChoice_NoneLabel"), ProfileChoiceKind.None);

    /// <summary>True for the synthetic "no profile" choice.</summary>
    public bool IsNone => Kind == ProfileChoiceKind.None;

    /// <summary>True for a real, saved profile.</summary>
    public bool IsReal => Kind == ProfileChoiceKind.Real;
}
