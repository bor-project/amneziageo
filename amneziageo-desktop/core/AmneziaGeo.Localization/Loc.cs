using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AmneziaGeo.Localization;

/// <summary>
/// App-wide localization access point built on the Microsoft localizer pattern
/// (<see cref="IStringLocalizerFactory"/> / <see cref="IStringLocalizer"/>, ResX-backed). The neutral
/// resources (<c>Strings.resx</c>) are English, so a key missing from the active culture's satellite
/// (<c>Strings.ru.resx</c>) falls back to English automatically - which is exactly the desired
/// "not in the dictionary -> default English" behaviour.
///
/// The culture is chosen at startup (saved preference -> system UI language -> English) and can be switched
/// live: <see cref="SetCulture"/> updates <see cref="CultureInfo.CurrentUICulture"/> and raises the "Item[]"
/// change, so every XAML binding to <c>this[key]</c> re-reads its translation without a restart. Only the UI
/// culture is changed, not <see cref="CultureInfo.CurrentCulture"/>, so number/date parsing is untouched.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Persisted language tokens the selector maps to: "" = follow system, else a supported code.</summary>
    public const string SystemToken = "";

    /// <summary>The UI cultures with a real translation; anything else resolves to English.</summary>
    public static readonly string[] Supported = ["en", "ru"];

    /// <summary>The shared instance XAML bindings and view models translate through.</summary>
    public static Loc Instance { get; } = new();

    private readonly IStringLocalizer _localizer;

    private Loc()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance);
        _localizer = factory.Create(typeof(Strings));
    }

    /// <summary>The active UI culture.</summary>
    public CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>Raised after the culture changes, for consumers that are not XAML bindings (e.g. the tray menu).</summary>
    public event Action? CultureChanged;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Indexer used by XAML bindings (<c>{l:Tr Key}</c> -> <c>Binding "[Key]" Source=Loc.Instance</c>).</summary>
    public string this[string key] => _localizer[key];

    /// <summary>Translates a key (code-side).</summary>
    public string Get(string key) => _localizer[key];

    /// <summary>Translates a formatted key with arguments (code-side).</summary>
    public string Get(string key, params object[] args) => _localizer.GetString(key, args);

    /// <summary>
    /// Resolves and applies the startup culture: the saved token when supported, else the OS UI language when
    /// it is supported, else English. Returns the chosen culture.
    /// </summary>
    public CultureInfo ApplyStartupCulture(string? saved)
    {
        var culture = Resolve(saved);
        Apply(culture);
        return culture;
    }

    /// <summary>Switches the active culture live and re-notifies every binding. Token "" follows the system.</summary>
    public void SetCulture(string? token)
    {
        Apply(Resolve(token));
    }

    // Maps a saved token to a culture: a supported code wins; "" / "system" / anything unknown follows the OS
    // UI language when it is supported, otherwise English (the ultimate fallback).
    private static CultureInfo Resolve(string? token)
    {
        var t = token?.Trim().ToLowerInvariant();
        if (t is "en" or "ru")
        {
            return CultureInfo.GetCultureInfo(t);
        }

        var system = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return CultureInfo.GetCultureInfo(system == "ru" ? "ru" : "en");
    }

    private void Apply(CultureInfo culture)
    {
        Culture = culture;
        // Resource lookups key off the UI culture; leave CurrentCulture (number/date formatting) as it was so
        // this never changes how values are parsed or written.
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        // "Item[]" is the convention for "every indexed value may have changed", so all {l:Tr} bindings re-read.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CultureChanged?.Invoke();
    }
}
