using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.Localization;

/// <summary>
/// XAML markup extension for a localized string.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    /// <summary>
    /// ctor
    /// </summary>
    public TrExtension()
    {
    }

    /// <summary>
    /// ctor
    /// </summary>
    public TrExtension(string key)
    {
        Key = key;
    }

    /// <summary>
    /// The resource key to translate.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
    }
}
