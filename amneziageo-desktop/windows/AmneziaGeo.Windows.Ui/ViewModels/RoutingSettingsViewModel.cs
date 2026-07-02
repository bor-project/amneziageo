using System.Text.Json;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The per-routing-list traffic editor shown in the Routing settings section: the preferred local DNS for
/// non-tunneled names, the bypass-exclusions list (domains kept on the local resolver, IP/CIDR routed
/// direct), and whether all UDP is forced through the tunnel. These used to live per-config; they now hang
/// off the routing list (keyed by its id) so the same config can be paired with different routing presets.
/// Loaded through the agent (get-routing-settings) and saved as a block (set-routing-settings); the list's
/// rule set lives separately in <see cref="RoutingListEditorViewModel"/>. Applies on the next connect.
/// </summary>
internal sealed partial class RoutingSettingsViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;

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

    // True while LoadAsync is seeding the fields, so echoing the loaded values back does not mark the editor
    // dirty. Edits the user makes after the load set Dirty, which the host flushes on a list switch.
    private bool _loading;

    /// <summary>
    /// ctor. Call <see cref="LoadAsync"/> after construction to populate the fields from the agent.
    /// </summary>
    public RoutingSettingsViewModel(AgentConnection connection, long listId)
    {
        _connection = connection;
        ListId = listId;
    }

    /// <summary>The routing list id these settings belong to.</summary>
    public long ListId { get; }

    /// <summary>
    /// Whether the user changed DNS / exclusions / all-UDP since the last load or save. The host flushes a
    /// dirty editor before switching to another list so an un-saved edit is not silently dropped.
    /// </summary>
    public bool Dirty { get; private set; }

    partial void OnLocalDnsChanged(string value) => MarkDirty();

    partial void OnExclusionsChanged(string value) => MarkDirty();

    partial void OnAllUdpChanged(bool value) => MarkDirty();

    private void MarkDirty()
    {
        if (!_loading)
        {
            Dirty = true;
        }
    }

    /// <summary>
    /// Fetches the stored settings for this list from the agent and fills the fields. Missing/unreadable
    /// settings leave the defaults (empty DNS/exclusions, all-UDP off).
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
                // best-effort: a malformed ack leaves the defaults
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
    /// Saves DNS + exclusions + all-UDP for this list as one block (mode stays "split"; full tunnel is a
    /// separate routing choice, not a list setting). An all-default block clears the row server-side.
    /// Applies on reconnect.
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
    /// Fetches the machine's currently-connected local subnets from the agent and merges them into the
    /// exclusions list (no duplicates). Nothing is saved here - the user reviews and presses «Сохранить».
    /// Mirrors the former per-config exclusions helper.
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
                ? $"Добавлено локальных сетей: {added.Count}. Проверьте список и нажмите «Сохранить»."
                : subnets.Length == 0
                    ? "Активные локальные сети не обнаружены."
                    : "Все локальные сети уже в списке.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
