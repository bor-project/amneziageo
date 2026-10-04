using Avalonia.Controls;
using Avalonia.Media;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Рисует окно в масштабе руки.
/// </summary>
internal static class HandFrame
{
    /// <summary>
    /// Оборачивает окно в увеличение и переносит обрезку с окна на обёртку.
    /// </summary>
    public static Control Enlarged(Control view, double scale)
    {
        if (scale == 1)
        {
            view.ClipToBounds = true;
            return view;
        }

        view.ClipToBounds = false;
        return new LayoutTransformControl
        {
            Child = view,
            LayoutTransform = new ScaleTransform(scale, scale),
            ClipToBounds = true,
        };
    }
}
