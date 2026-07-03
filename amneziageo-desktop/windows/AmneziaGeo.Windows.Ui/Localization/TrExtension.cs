using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.Localization;

/// <summary>
/// XAML markup extension for a localized string: <c>{l:Tr General_Section}</c>. It returns a one-way binding
/// to <c>Loc.Instance[key]</c>, so a live culture switch (which raises "Item[]") re-reads the translation with
/// no reload. A key absent from the active culture falls back to English (the neutral resources).
/// </summary>
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
        return new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
    }
}
