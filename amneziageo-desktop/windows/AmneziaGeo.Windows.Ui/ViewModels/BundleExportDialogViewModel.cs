using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// View model for the selective bundle export dialog (#91): the user checks which profiles, configs, and
/// routing lists to include, then exports them as one portable JSON file via <see cref="IpcContract.OpExportBundle"/>.
/// Distinct from <see cref="ExportDialogViewModel"/> (a single config's .conf/vpn:// share) - this picks
/// from a tree across all three catalogues at once. Built directly from the window's already-loaded
/// snapshot collections, so opening the dialog needs no extra IPC round trip.
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
    /// ctor. Builds the three checkable lists from the window's live snapshot collections and wires the
    /// profile -&gt; config/routing-list cascade.
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
            var item = new BundleItem { Name = list.Name, Detail = list.Detail };
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

    /// <summary>The "ПРОФИЛИ" section rows.</summary>
    public ObservableCollection<BundleItem> ProfileItems { get; } = [];

    /// <summary>The "КОНФИГУРАЦИИ" section rows.</summary>
    public ObservableCollection<BundleItem> ConfigItems { get; } = [];

    /// <summary>The "МАРШРУТИЗАЦИЯ" section rows.</summary>
    public ObservableCollection<BundleItem> RoutingItems { get; } = [];

    /// <summary>A suggested file name for the exported bundle.</summary>
    public string SuggestedFileName => "amneziageo-bundle.agbundle.json";

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
            var selection = new SelectionPayload(
                [.. ProfileItems.Where(i => i.IsChecked).Select(i => i.Name)],
                [.. ConfigItems.Where(i => i.IsChecked).Select(i => i.Name)],
                [.. RoutingItems.Where(i => i.IsChecked).Select(i => i.Name)]);
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

    // Mirrors the selection JSON the agent parses (AgentStatusBroker.SelectionRequest): { profiles, configs,
    // routingLists }, serialized camelCase so the wire shape matches the brief/contract documentation exactly.
    private sealed record SelectionPayload(string[] Profiles, string[] Configs, string[] RoutingLists);
}
