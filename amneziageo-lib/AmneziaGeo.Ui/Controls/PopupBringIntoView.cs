using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Keeps bring-into-view requests raised inside a popup from reaching the page under it.
/// </summary>
internal static class PopupBringIntoView
{
    /// <summary>
    /// Stops the requests at every popup.
    /// </summary>
    public static void Register()
    {
        Control.RequestBringIntoViewEvent.AddClassHandler<Popup>((_, e) => e.Handled = true);
    }
}
