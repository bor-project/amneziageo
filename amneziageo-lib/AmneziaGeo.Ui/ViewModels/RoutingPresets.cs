using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Готовый набор правил: чем заполнить корзины и куда направить остальной трафик.
/// </summary>
internal sealed record RoutingPreset(
    string Key,
    string[] Proxy,
    string[] Direct,
    bool UseGlobalProxy,
    bool LocalSubnets,
    bool NeedsCountry);

/// <summary>
/// Наборы, предлагаемые при создании списка.
/// </summary>
internal static class RoutingPresets
{
    /// <summary>
    /// Правило переключателя «Резать рекламу».
    /// </summary>
    public const string AdsRule = "geosite:category-ads-all";

    /// <summary>
    /// Заготовленные списки недоступных сервисов.
    /// </summary>
    public static readonly string[] ClosedRules = ["geosite:ru-blocked", "geoip:ru-blocked"];

    /// <summary>
    /// Заготовленные списки сервисов, работающих только изнутри своей страны.
    /// </summary>
    public static readonly string[] InsideRules = ["geosite:ru-available-only-inside"];

    /// <summary>
    /// Наборы в порядке показа: сверху точечный, снизу самый грубый.
    /// </summary>
    public static readonly RoutingPreset[] All =
    [
        new(
            "Closed",
            [
                .. ClosedRules,
                "geosite:youtube",
                "geosite:meta",
                "geosite:twitter",
                "geosite:discord",
                "geosite:openai",
                "geoip:telegram",
            ],
            [.. InsideRules, "geoip:{0}"],
            false,
            true,
            false),
        new(
            "AllButLocal",
            [],
            ["geoip:{0}"],
            true,
            true,
            true),
        new(
            "Everything",
            [],
            [],
            true,
            true,
            false),
    ];

    /// <summary>
    /// Страна устройства, без запроса разрешений.
    /// </summary>
    public static string CurrentCountry()
    {
        var region = CurrentRegion();
        return region.Length == 0 ? CountryOfLanguage() : region;
    }

    /// <summary>
    /// Разворачивает шаблоны правил по выбранным регионам.
    /// </summary>
    public static IReadOnlyList<string> Rules(string[] templates, IReadOnlyList<string> regions)
    {
        var rules = new List<string>();
        foreach (var template in templates)
        {
            if (!template.Contains("{0}", StringComparison.Ordinal))
            {
                rules.Add(template);
                continue;
            }

            foreach (var region in regions)
            {
                rules.Add(string.Format(CultureInfo.InvariantCulture, template, region));
            }
        }

        return rules;
    }

    /// <summary>
    /// Имя региона латиницей: шрифт приложения несёт её на любом устройстве.
    /// </summary>
    public static string RegionName(string code) => Region(code)?.EnglishName ?? code;

    /// <summary>
    /// Имя региона на его собственном языке, для поиска.
    /// </summary>
    public static string RegionNativeName(string code) => Region(code)?.DisplayName ?? code;

    private static RegionInfo? Region(string code)
    {
        if (code.Length != 2)
        {
            return null;
        }

        try
        {
            return new RegionInfo(code);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CurrentRegion()
    {
        try
        {
            return RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string CountryOfLanguage()
    {
        try
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
            {
                "ru" => "ru",
                "be" => "by",
                "kk" => "kz",
                "uz" => "uz",
                "az" => "az",
                "tr" => "tr",
                "fa" => "ir",
                "zh" => "cn",
                "vi" => "vn",
                "id" => "id",
                "ur" => "pk",
                "my" => "mm",
                "tk" => "tm",
                _ => string.Empty,
            };
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Карточка набора на экране выбора.
/// </summary>
internal sealed class RoutingPresetItemViewModel(RoutingPreset preset)
{
    /// <summary>
    /// Набор, стоящий за карточкой.
    /// </summary>
    public RoutingPreset Preset => preset;

    /// <summary>
    /// Имя набора, оно же имя создаваемого списка.
    /// </summary>
    public string Name => Loc.Instance.Get($"Preset_{preset.Key}Name");

    /// <summary>
    /// Что идёт через VPN, а что напрямую.
    /// </summary>
    public string Hint => Loc.Instance.Get($"Preset_{preset.Key}Hint");

    /// <summary>
    /// Примеры того, что попадёт в туннель.
    /// </summary>
    public IReadOnlyList<string> Chips => Loc.Instance
        .Get($"Preset_{preset.Key}Chips")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Строка региона на экране выбора.
/// </summary>
internal sealed partial class RegionItemViewModel : ObservableObject
{
    private readonly Action<RegionItemViewModel> _toggled;

    /// <summary>
    /// ctor
    /// </summary>
    public RegionItemViewModel(string code, bool isPicked, Action<RegionItemViewModel> toggled)
    {
        Code = code;
        Name = RoutingPresets.RegionName(code);
        _isPicked = isPicked;
        _toggled = toggled;
    }

    /// <summary>
    /// Код региона, каким он стоит в правиле geoip.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Имя региона.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Код региона у имени; у категории без имени пуст.
    /// </summary>
    public string Badge => string.Equals(Name, Code, StringComparison.OrdinalIgnoreCase)
        ? string.Empty
        : Code.ToUpperInvariant();

    /// <summary>
    /// Отмечен ли регион.
    /// </summary>
    [ObservableProperty]
    private bool _isPicked;

    partial void OnIsPickedChanged(bool value) => _toggled(this);
}
