using Avalonia.Media;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Maps connection status tokens to localized labels and badge colors.
/// </summary>
internal static class StatusLabels
{
    private static readonly IBrush _green = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
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
            ConnectionStatus.Connected => "Подключено",
            ConnectionStatus.Connecting => "Подключается",
            ConnectionStatus.Degraded => "Деградация",
            ConnectionStatus.Failover => "Переключение",
            ConnectionStatus.Disconnected => "Отключено",
            ConnectionStatus.Preempted => "Вытеснено",
            ConnectionStatus.Failed => "Сбой",
            _ => "Простаивает",
        };
    }

    /// <summary>
    /// Returns the badge color for a status token.
    /// </summary>
    public static IBrush Brush(string status)
    {
        return status switch
        {
            ConnectionStatus.Connected => _green,
            ConnectionStatus.Connecting or ConnectionStatus.Failover or ConnectionStatus.Degraded => _amber,
            ConnectionStatus.Failed => _red,
            _ => _gray,
        };
    }
}
