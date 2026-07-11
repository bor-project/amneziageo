using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The kind of a ProfileChoice exposed to a profile combo box.
/// </summary>
internal enum ProfileChoiceKind
{
    /// <summary>
    /// A real, saved profile selected by its name.
    /// </summary>
    Real,

    /// <summary>
    /// The synthetic no-profile choice, nothing selected yet.
    /// </summary>
    None,
}

/// <summary>
/// A profile pick for the main-window and Profile-section combo boxes. Identity is the stable key, Name is
/// observable for a live rename preview, Kind distinguishes the synthetic none choice. The host resolves a
/// real choice to its ProfileItemViewModel by Identity.
/// </summary>
internal sealed partial class ProfileChoice : ObservableObject
{
    public ProfileChoice(string identity, ProfileChoiceKind kind = ProfileChoiceKind.Real)
    {
        Identity = identity;
        Kind = kind;
        _name = identity;
    }

    /// <summary>
    /// The stable key - the real profile name, unchanged by a live-typed rename.
    /// </summary>
    public string Identity { get; }

    /// <summary>
    /// Whether this is a real profile or the synthetic none choice.
    /// </summary>
    public ProfileChoiceKind Kind { get; }

    /// <summary>
    /// The label shown in the combo. Tracks the Profile editor's name field live during a rename, snaps
    /// back when saved or dropped.
    /// </summary>
    [ObservableProperty]
    private string _name;

    /// <summary>
    /// The synthetic no-profile choice, selectable when nothing is picked.
    /// </summary>
    public static ProfileChoice None { get; } = new(Loc.Instance.Get("ProfileChoice_NoneLabel"), ProfileChoiceKind.None);

    /// <summary>
    /// True for the synthetic no-profile choice.
    /// </summary>
    public bool IsNone => Kind == ProfileChoiceKind.None;

    /// <summary>
    /// True for a real, saved profile.
    /// </summary>
    public bool IsReal => Kind == ProfileChoiceKind.Real;
}
