using Avalonia.Media;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Maps connection status tokens to localized labels and badge colors.
/// </summary>
internal static class StatusLabels
{
    private static readonly IBrush _blue = new SolidColorBrush(Color.FromRgb(0x2A, 0x6F, 0xDB));
    private static readonly IBrush _amber = new SolidColorBrush(Color.FromRgb(0xC8, 0x7A, 0x00));
    private static readonly IBrush _red = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush _gray = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

    /// <summary>
    /// Returns the localized label for a status token.
    /// </summary>
    public static string Text(string status)
    {
        return status switch
        {
            ConnectionStatus.Connected => Loc.Instance.Get("Status_Connected"),
            ConnectionStatus.Connecting => Loc.Instance.Get("Status_Connecting"),
            ConnectionStatus.Disconnecting => Loc.Instance.Get("Status_Disconnecting"),
            ConnectionStatus.Disconnected => Loc.Instance.Get("Status_Disconnected"),
            ConnectionStatus.Preempted => Loc.Instance.Get("Status_Preempted"),
            ConnectionStatus.Failed => Loc.Instance.Get("Status_Failed"),
            _ => Loc.Instance.Get("Status_Idle"),
        };
    }

    /// <summary>
    /// Returns the badge color for a status token.
    /// </summary>
    public static IBrush Brush(string status)
    {
        return status switch
        {
            ConnectionStatus.Connected => _blue,
            ConnectionStatus.Connecting or ConnectionStatus.Disconnecting => _amber,
            ConnectionStatus.Failed => _red,
            _ => _gray,
        };
    }
}
