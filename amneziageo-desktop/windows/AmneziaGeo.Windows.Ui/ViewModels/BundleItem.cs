using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A single checkable row in the selective export tree: a profile, a config, or a routing list, by name.
/// IsLocked disables the checkbox while a checked profile depends on it, without forcing it unchecked.
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
