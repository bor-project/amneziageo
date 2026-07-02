using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A single checkable row in the selective export tree (#91): a profile, a config, or a routing list, by
/// name. <see cref="IsLocked"/> disables the checkbox while a checked profile depends on it (the export
/// dialog's cascade - see <see cref="CheckedChanged"/>), without forcing it unchecked, so the user can still
/// export it standalone.
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
    /// Invoked after <see cref="IsChecked"/> changes by user action. The owning dialog view model subscribes
    /// per item at construction to apply the profile -&gt; config/routing-list cascade and recompute whether
    /// the Export button can run - kept as a plain delegate (the project's established style for this kind
    /// of per-row callback, e.g. <c>BalancerItemViewModel</c>'s constructor delegates) rather than a heavier
    /// messaging system.
    /// </summary>
    public Action<bool>? CheckedChanged { get; set; }

    partial void OnIsCheckedChanged(bool value)
    {
        CheckedChanged?.Invoke(value);
    }
}
