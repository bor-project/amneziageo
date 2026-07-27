using Avalonia.Controls;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Profile screen view.
/// </summary>
internal sealed partial class ProfileView : UserControl
{
    private readonly HeaderReflow _header;

    /// <summary>
    /// ctor
    /// </summary>
    public ProfileView()
    {
        InitializeComponent();
        _header = new HeaderReflow(HeaderGrid, HeaderTabs, PickerHost, Picker, PickerLabelFloat, PickerLabelInline,
            () => (DataContext as ProfileViewModel)?.IsCompact ?? false);
        DataContextChanged += (_, _) => _header.Apply();
    }
}
