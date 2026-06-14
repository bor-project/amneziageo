using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A selectable balancer member in the add dialog.
/// </summary>
internal sealed partial class MemberChoice(string name) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// The configuration name.
    /// </summary>
    public string Name { get; } = name;
}
