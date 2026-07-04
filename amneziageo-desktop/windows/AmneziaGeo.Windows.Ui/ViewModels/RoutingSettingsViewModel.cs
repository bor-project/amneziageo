using System.Text.Json;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The per-routing-list traffic editor shown in the Routing settings section: preferred local DNS for
/// non-tunneled names, the bypass-exclusions list, and whether all UDP is forced through the tunnel.
/// Loaded through the agent and saved as a block; the list's rule set lives separately in
/// RoutingListEditorViewModel. Applies on the next connect.
/// </summary>
internal sealed partial class RoutingSettingsViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly Debouncer _saveDebounce;

    [ObservableProperty]
    private string _localDns = string.Empty;

    [ObservableProperty]
    private string _exclusions = string.Empty;

    [ObservableProperty]
    private bool _allUdp;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // True while LoadAsync seeds the fields.
    private bool _loading;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingSettingsViewModel(AgentConnection connection, long listId)
    {
        _connection = connection;
        ListId = listId;
        _saveDebounce = new Debouncer(700, SaveAsync);
    }

    /// <summary>
    /// Persist a pending debounced edit at once.
    /// </summary>
    public void FlushPendingSave() => _saveDebounce.Flush();

    /// <summary>
    /// The routing list id these settings belong to.
    /// </summary>
    public long ListId { get; }

    /// <summary>
    /// True if the user changed DNS / exclusions / all-UDP since the last load or save.
    /// </summary>
    public bool Dirty { get; private set; }

    partial void OnLocalDnsChanged(string value) => OnEdited();

    partial void OnExclusionsChanged(string value) => OnEdited();

    partial void OnAllUdpChanged(bool value) => OnEdited();

    // Persist automatically on any edit; the save is debounced.
    private void OnEdited()
    {
        if (_loading)
        {
            return;
        }

        Dirty = true;
        _saveDebounce.Schedule();
    }

    /// <summary>
    /// Fetches the stored settings for this list from the agent and fills the fields. Missing/unreadable
    /// settings leave the defaults.
    /// </summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        _loading = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetRoutingSettings,
                [ListId.ToString()]));
            if (!ack.Ok)
            {
                StatusMessage = ack.Message;
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(ack.Message);
                var root = doc.RootElement;
                LocalDns = root.TryGetProperty("localDns", out var dns) ? dns.GetString() ?? string.Empty : string.Empty;
                Exclusions = root.TryGetProperty("exclusions", out var ex) ? ex.GetString() ?? string.Empty : string.Empty;
                AllUdp = root.TryGetProperty("allUdp", out var udp) && udp.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                // A malformed ack leaves the defaults.
            }
        }
        finally
        {
            _loading = false;
            Dirty = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves DNS + exclusions + all-UDP for this list as one block; applies on reconnect.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetRoutingSettings,
            [
                ListId.ToString(),
                (LocalDns ?? string.Empty).Trim(),
                (Exclusions ?? string.Empty).Trim(),
                AllUdp ? "on" : "off",
                "split",
            ]));
            StatusMessage = ack.Message;
            if (ack.Ok)
            {
                Dirty = false;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fetches the machine's local subnets from the agent and merges them into the exclusions list.
    /// </summary>
    [RelayCommand]
    private async Task AddLocalSubnetsAsync()
    {
        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListLocalSubnets, []));
            if (!ack.Ok)
            {
                StatusMessage = ack.Message;
                return;
            }

            var subnets = ack.Message
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var lines = (Exclusions ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            var seen = new HashSet<string>(lines, StringComparer.OrdinalIgnoreCase);

            var added = subnets.Where(subnet => seen.Add(subnet)).ToList();
            if (added.Count > 0)
            {
                lines.AddRange(added);
                Exclusions = string.Join(Environment.NewLine, lines);
            }

            StatusMessage = added.Count > 0
                ? Loc.Instance.Get("RoutingSettings_LocalSubnetsAdded", added.Count)
                : subnets.Length == 0
                    ? Loc.Instance.Get("RoutingSettings_NoActiveLocalSubnets")
                    : Loc.Instance.Get("RoutingSettings_AllLocalSubnetsPresent");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
