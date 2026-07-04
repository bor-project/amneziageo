using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AmneziaGeo.Localization;

/// <summary>
/// App localization access point over the Microsoft localizer.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>
    /// Persisted language token: empty follows system, else a supported code.
    /// </summary>
    public const string SystemToken = "";

    /// <summary>
    /// Supported UI cultures; others fall back to English.
    /// </summary>
    public static readonly string[] Supported = ["en", "ru"];

    /// <summary>
    /// Shared instance for XAML bindings.
    /// </summary>
    public static Loc Instance { get; } = new();

    private readonly IStringLocalizer _localizer;

    private Loc()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance);
        _localizer = factory.Create(typeof(Strings));
    }

    /// <summary>
    /// Active UI culture.
    /// </summary>
    public CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// Raised after a culture change for non-XAML consumers.
    /// </summary>
    public event Action? CultureChanged;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Indexer for XAML bindings.
    /// </summary>
    public string this[string key] => _localizer[key];

    /// <summary>
    /// Translates a key.
    /// </summary>
    public string Get(string key) => _localizer[key];

    /// <summary>
    /// Translates a formatted key with arguments.
    /// </summary>
    public string Get(string key, params object?[] args) => _localizer.GetString(key, (object[])args);

    /// <summary>
    /// Resolves and applies the startup culture.
    /// </summary>
    public CultureInfo ApplyStartupCulture(string? saved)
    {
        var culture = Resolve(saved);
        Apply(culture);
        return culture;
    }

    /// <summary>
    /// Switches the active culture live.
    /// </summary>
    public void SetCulture(string? token)
    {
        Apply(Resolve(token));
    }

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
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CultureChanged?.Invoke();
    }
}
