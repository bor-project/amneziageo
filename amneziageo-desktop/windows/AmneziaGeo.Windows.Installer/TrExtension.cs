using System;
using System.Windows.Markup;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// WPF markup extension for a localized installer string.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
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
    /// Resource key to translate.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Loc.Instance.Get(Key);
    }
}
