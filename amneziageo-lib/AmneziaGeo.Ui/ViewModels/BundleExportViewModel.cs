using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// View model for the bundle export view.
/// </summary>
internal sealed partial class BundleExportViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions _selectionOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAgentConnection _connection;

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    private bool _isCompact;

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
    public BundleExportViewModel(
        IAgentConnection connection,
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
    }

    public ObservableCollection<BundleItem> ConfigItems { get; } = [];

    public ObservableCollection<BundleItem> RoutingItems { get; } = [];

    public string SuggestedFileName => "amneziageo-bundle.agbundle.json";

    /// <summary>
    /// Whether the platform hands an export to another application.
    /// </summary>
    public bool CanSendExport => PlatformExportHost.CanSend;

    /// <summary>
    /// Fetches each routing list's rule tokens so the export tree can offer per-rule exclusion.
    /// Tokens match exactly what the agent exports (both go through GeoConfigurator.FormatWithRole, so they
    /// carry the bucket prefix), so the selection can filter by token string. Call once after construction,
    /// before showing the dialog.
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
        CanExport = ConfigItems.Any(i => i.IsChecked) || RoutingItems.Any(i => i.IsChecked);
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
                // Emit the keep-list for any list with an excluded rule. The agent applies it solely to lists it
                // actually exports, so a spare entry is harmless.
                if (item.Rules.Any(r => !r.IsChecked))
                {
                    routingRules[item.Name] = [.. item.Rules.Where(r => r.IsChecked).Select(r => r.Token)];
                }
            }

            var selection = new SelectionPayload(
                [.. ConfigItems.Where(i => i.IsChecked).Select(i => i.Name)],
                [.. RoutingItems.Where(i => i.IsChecked).Select(i => i.Name)],
                routingRules.Count > 0 ? routingRules : null);
            var json = JsonSerializer.Serialize(selection, _selectionOptions);

            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpExportBundle, [json]));
            if (!ack.Ok)
            {
                StatusMessage = IpcMessage.TryParse(ack.Message, out var key, out var args)
                    ? Loc.Instance.Get(key, args)
                    : ack.Message;
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
        string[] Configs,
        string[] RoutingLists,
        Dictionary<string, string[]>? RoutingRules);
}
