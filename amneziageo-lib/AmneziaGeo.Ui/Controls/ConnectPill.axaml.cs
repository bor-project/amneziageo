using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// The connect / disconnect pill of the header: the state circle, the state text, and the retry counter.
/// </summary>
internal sealed partial class ConnectPill : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public ConnectPill()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The pill button: the focus target a host seats a remote on.
    /// </summary>
    public Button Toggle => TogglePart;
}
