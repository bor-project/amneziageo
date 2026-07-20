using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// A single checkable row in the selective export tree: a profile, a config, or a routing list, by name.
/// IsLocked disables the checkbox while a checked profile depends on it, without forcing it unchecked.
/// A routing list also carries its rules so the user can drop machine-specific ones before export.
/// </summary>
internal sealed partial class BundleItem : ViewModelBase
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private bool _hasRules;

    /// <summary>
    /// Store id of a routing list, so its rules can be fetched; 0 for profiles and configs.
    /// </summary>
    public long ListId { get; set; }

    /// <summary>
    /// Rule tokens under a routing list; unchecked ones are excluded from the exported list.
    /// </summary>
    public ObservableCollection<BundleRuleItem> Rules { get; } = [];

    /// <summary>
    /// Invoked after IsChecked changes by user action. The owning dialog subscribes per item to apply the
    /// profile to config/routing-list cascade and recompute whether Export can run.
    /// </summary>
    public Action<bool>? CheckedChanged { get; set; }

    partial void OnIsCheckedChanged(bool value)
    {
        CheckedChanged?.Invoke(value);
    }
}

/// <summary>
/// A checkable rule token under a routing list. Unchecking drops just this rule from the exported
/// list; the rest still travels. Tokens match what the agent exports (both use GeoConfigurator.FormatWithRole).
/// </summary>
internal sealed partial class BundleRuleItem : ViewModelBase
{
    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _isChecked = true;

    /// <summary>
    /// App-path rules are the usual machine-specific entries stripped before sharing (see #127).
    /// </summary>
    public bool IsAppRule => Token.StartsWith("app:", StringComparison.Ordinal);
}
