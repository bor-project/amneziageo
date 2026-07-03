using System;
using System.Windows.Markup;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// WPF markup extension for a localized installer string: <c>{l:Tr Installer_Xxx}</c>. The installer's UI
/// culture is fixed at startup (the system UI language, English as the fallback) and never switched, so this
/// resolves the translation once at parse time and returns the plain string - no binding needed.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    /// <summary>ctor for the <c>{l:Tr}</c> form with the key set via the Key property.</summary>
    public TrExtension()
    {
    }

    /// <summary>ctor for the positional <c>{l:Tr Key}</c> form.</summary>
    public TrExtension(string key)
    {
        Key = key;
    }

    /// <summary>The resource key to translate.</summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Loc.Instance.Get(Key);
    }
}
