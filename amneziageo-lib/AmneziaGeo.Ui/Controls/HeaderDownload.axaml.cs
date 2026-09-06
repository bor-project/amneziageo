using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// The running update download in the header: the progress bar with its percent and the cancel link.
/// </summary>
internal sealed partial class HeaderDownload : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public HeaderDownload()
    {
        InitializeComponent();
    }
}
