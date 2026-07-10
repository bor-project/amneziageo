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
internal sealed partial class RoutingSettingsViewModel : ViewModelBase, IEditScope
{
    private readonly AgentConnection _connection;

    // Baseline captured on load / commit; the fields are dirty when they differ from it (#143).
    private string _baseLocalDns = string.Empty;
    private string _baseExclusions = string.Empty;
    private bool _baseAllUdp;
    private bool _baseUseIpv6;

    [ObservableProperty]
    private string _localDns = string.Empty;

    [ObservableProperty]
    private string _exclusions = string.Empty;

    [ObservableProperty]
    private bool _allUdp;

    [ObservableProperty]
    private bool _useIpv6;

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
    }

    /// <summary>
    /// The routing list id these settings belong to.
    /// </summary>
    public long ListId { get; private set; }

    /// <summary>
    /// Retargets these settings at a (newly-created) list id before committing them. Used when a new-list draft
    /// is first saved: the draft's settings were built against id 0 and must now target the real id (#5).
    /// </summary>
    public void Retarget(long id) => ListId = id;

    /// <summary>
    /// True when DNS / exclusions / all-UDP differ from the last loaded or committed values (#143).
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <inheritdoc />
    public event EventHandler? DirtyChanged;

    partial void OnLocalDnsChanged(string value) => OnEdited();

    partial void OnExclusionsChanged(string value) => OnEdited();

    partial void OnAllUdpChanged(bool value) => OnEdited();

    partial void OnUseIpv6Changed(bool value) => OnEdited();

    // A field changed: recompute dirtiness against the baseline. No auto-save - the header Save/Cancel commits
    // or reverts the whole item at once (#143), which is what keeps a mid-type edit from persisting per keystroke.
    private void OnEdited() => RecomputeDirty();

    private void RecomputeDirty()
    {
        if (_loading)
        {
            return;
        }

        // Any edit clears a stale validation / status line (#3).
        StatusMessage = string.Empty;

        var dirty = !string.Equals(LocalDns, _baseLocalDns, StringComparison.Ordinal)
            || !string.Equals(Exclusions, _baseExclusions, StringComparison.Ordinal)
            || AllUdp != _baseAllUdp
            || UseIpv6 != _baseUseIpv6;
        if (dirty != IsDirty)
        {
            IsDirty = dirty;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public bool CanCommit() => true;

    /// <inheritdoc />
    public void CaptureBaseline()
    {
        _baseLocalDns = LocalDns ?? string.Empty;
        _baseExclusions = Exclusions ?? string.Empty;
        _baseAllUdp = AllUdp;
        _baseUseIpv6 = UseIpv6;
        if (IsDirty)
        {
            IsDirty = false;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Revert()
    {
        _loading = true;
        try
        {
            LocalDns = _baseLocalDns;
            Exclusions = _baseExclusions;
            AllUdp = _baseAllUdp;
            UseIpv6 = _baseUseIpv6;
            StatusMessage = string.Empty;
        }
        finally
        {
            _loading = false;
        }

        RecomputeDirty();
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
                UseIpv6 = root.TryGetProperty("useIpv6", out var v6) && v6.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                // A malformed ack leaves the defaults.
            }
        }
        finally
        {
            _loading = false;
            CaptureBaseline();
            IsBusy = false;
        }
    }

    /// <summary>
    /// Persists DNS + exclusions + all-UDP for this list as one block (#143 header Save); applies on reconnect.
    /// Returns whether the agent accepted it.
    /// </summary>
    public async Task<bool> CommitAsync()
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
                UseIpv6 ? "on" : "off",
            ]));
            StatusMessage = ack.Message;
            return ack.Ok;
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
