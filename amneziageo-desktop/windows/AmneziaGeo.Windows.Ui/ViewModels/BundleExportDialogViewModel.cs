using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// View model for the bundle export dialog.
/// </summary>
internal sealed partial class BundleExportDialogViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions _selectionOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AgentConnection _connection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _canExport;

    [ObservableProperty]
    private bool _isExported;

    [ObservableProperty]
    private string _payload = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public BundleExportDialogViewModel(
        AgentConnection connection,
        IReadOnlyList<BalancerItemViewModel> balancers,
        IReadOnlyList<ConfigItemViewModel> configs,
        IReadOnlyList<RoutingListSummaryViewModel> routingLists)
    {
        _connection = connection;

        foreach (var config in configs)
        {
            var item = new BundleItem { Name = config.Name, Detail = config.Endpoint };
            Wire(item);
            ConfigItems.Add(item);
        }

        foreach (var list in routingLists)
        {
            var item = new BundleItem { Name = list.Name, Detail = list.Detail, ListId = list.Id };
            Wire(item);
            RoutingItems.Add(item);
        }

        foreach (var balancer in balancers)
        {
            var item = new BundleItem { Name = balancer.Name, Detail = ProfileDetail(balancer) };

            // Resolved once, by name, from the collections just built above - the profile's checkbox then
            // drives these two dependents directly (cascade described on BundleItem.CheckedChanged).
            var dependentConfig = balancer.Config.Length > 0
                ? ConfigItems.FirstOrDefault(c => string.Equals(c.Name, balancer.Config, StringComparison.Ordinal))
                : null;
            var dependentRouting = balancer.SelectedRoutingList.IsReal
                ? RoutingItems.FirstOrDefault(r => string.Equals(r.Name, balancer.SelectedRoutingList.Name, StringComparison.Ordinal))
                : null;

            Wire(item, isChecked =>
            {
                if (isChecked)
                {
                    if (dependentConfig is not null)
                    {
                        dependentConfig.IsChecked = true;
                        dependentConfig.IsLocked = true;
                    }

                    if (dependentRouting is not null)
                    {
                        dependentRouting.IsChecked = true;
                        dependentRouting.IsLocked = true;
                    }
                }
                else
                {
                    // Unlock without forcing unchecked - the user may still want the standalone object.
                    if (dependentConfig is not null)
                    {
                        dependentConfig.IsLocked = false;
                    }

                    if (dependentRouting is not null)
                    {
                        dependentRouting.IsLocked = false;
                    }
                }
            });

            ProfileItems.Add(item);
        }
    }

    public ObservableCollection<BundleItem> ProfileItems { get; } = [];

    public ObservableCollection<BundleItem> ConfigItems { get; } = [];

    public ObservableCollection<BundleItem> RoutingItems { get; } = [];

    public string SuggestedFileName => "amneziageo-bundle.agbundle.json";

    /// <summary>
    /// Fetches each routing list's rule tokens so the export tree can offer per-rule exclusion.
    /// Tokens match exactly what the agent exports (both go through GeoConfigurator.Format), so the
    /// selection can filter by token string. Call once after construction, before showing the dialog.
    /// </summary>
    public async Task LoadRoutingRulesAsync()
    {
        foreach (var item in RoutingItems)
        {
            if (item.ListId <= 0)
            {
                continue;
            }

            var detail = await _connection.SendCommandAsync(
                new IpcCommand(IpcContract.OpGetRoutingList, [item.ListId.ToString(CultureInfo.InvariantCulture)]));
            if (!detail.Ok)
            {
                continue;
            }

            foreach (var token in detail.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                item.Rules.Add(new BundleRuleItem { Token = token });
            }

            item.HasRules = item.Rules.Count > 0;
        }
    }

    private void Wire(BundleItem item, Action<bool>? cascade = null)
    {
        item.CheckedChanged = value =>
        {
            cascade?.Invoke(value);
            RecomputeCanExport();
        };
    }

    private void RecomputeCanExport()
    {
        CanExport = ProfileItems.Any(i => i.IsChecked) || ConfigItems.Any(i => i.IsChecked) || RoutingItems.Any(i => i.IsChecked);
    }

    private static string ProfileDetail(BalancerItemViewModel balancer)
    {
        var parts = new List<string>();
        if (balancer.Config.Length > 0)
        {
            parts.Add(balancer.Config);
        }

        if (balancer.SelectedRoutingList.IsReal)
        {
            parts.Add(balancer.SelectedRoutingList.Name);
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : Loc.Instance.Get("BundleExportVm_NoConfiguration");
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        IsBusy = true;
        try
        {
            // Per-list rule filter: only lists that ship AND have at least one excluded rule need an
            // explicit keep-list. An absent entry tells the agent to keep every rule (backward compatible).
            var routingRules = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var item in RoutingItems)
            {
                // Emit the keep-list for any list with an excluded rule - including one pulled in only by a
                // checked profile. The agent applies it solely to lists it actually exports, so a spare
                // entry is harmless, and a profile-bound list never loses its filter.
                if (item.Rules.Any(r => !r.IsChecked))
                {
                    routingRules[item.Name] = [.. item.Rules.Where(r => r.IsChecked).Select(r => r.Token)];
                }
            }

            var selection = new SelectionPayload(
                [.. ProfileItems.Where(i => i.IsChecked).Select(i => i.Name)],
                [.. ConfigItems.Where(i => i.IsChecked).Select(i => i.Name)],
                [.. RoutingItems.Where(i => i.IsChecked).Select(i => i.Name)],
                routingRules.Count > 0 ? routingRules : null);
            var json = JsonSerializer.Serialize(selection, _selectionOptions);

            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpExportBundle, [json]));
            if (!ack.Ok)
            {
                StatusMessage = ack.Message;
                return;
            }

            Payload = ack.Message;
            StatusMessage = string.Empty;
            IsExported = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Selection JSON sent to the agent (camelCase). RoutingRules maps a routing list name to the rule
    // tokens to KEEP; absent list = keep all its rules.
    private sealed record SelectionPayload(
        string[] Profiles,
        string[] Configs,
        string[] RoutingLists,
        Dictionary<string, string[]>? RoutingRules);
}
