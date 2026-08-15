using System;
using Avalonia.Markup.Xaml;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// XAML markup extension for a size that stays as laid out, whatever scale the head is drawn at.
/// </summary>
public sealed class UnscaledExtension : MarkupExtension
{
    /// <summary>
    /// ctor
    /// </summary>
    public UnscaledExtension()
    {
    }

    /// <summary>
    /// ctor
    /// </summary>
    public UnscaledExtension(double size)
    {
        Size = size;
    }

    /// <summary>
    /// The size the value is laid out at.
    /// </summary>
    public double Size { get; set; }

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Size / UiPlatform.HandScale;
    }
}
