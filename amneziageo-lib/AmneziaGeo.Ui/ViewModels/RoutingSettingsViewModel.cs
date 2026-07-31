using System.Text.Json;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// The per-routing-list traffic editor shown in the Routing settings section: the global-proxy flag and all-UDP.
/// Loaded through the agent and saved as a block; the list's rule set (with its Direct bypass bucket) lives
/// separately in RoutingListEditorViewModel. Applies on the next connect. IPv6 is per-config now (Config screen).
/// </summary>
internal sealed partial class RoutingSettingsViewModel : ViewModelBase, IEditScope
{
    private readonly IAgentConnection _connection;

    // Baseline captured on load / commit; the fields are dirty when they differ from it (#143).
    private bool _baseAllUdp;
    private bool _baseUseGlobalProxy;
    private string _baseRouteTtl = "300";

    [ObservableProperty]
    private bool _allUdp;

    [ObservableProperty]
    private bool _useGlobalProxy;

    // Agent-wide, not per-list: pushed straight through as a setting instead of the routing block. Seconds,
    // 0 (never held) to int.MaxValue.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteTtlInvalid))]
    private string _routeTtl = "300";

    /// <summary>
    /// Whether the entered lifetime is not a whole number of seconds in range.
    /// </summary>
    public bool RouteTtlInvalid => !TryParseTtl(RouteTtl, out _);

    private static bool TryParseTtl(string text, out int seconds)
    {
        return SettingKeys.TryParseRouteTtl(text, out seconds);
    }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // True while LoadAsync seeds the fields.
    private bool _loading;

    // Autosave: toggles persist at once; a reconnect need surfaces via the standard banner.
    private bool _committing;
    private bool _commitPending;

    /// <summary>
    /// When set, edits persist through the agent as they happen.
    /// </summary>
    public bool AutoSave { get; set; }

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingSettingsViewModel(IAgentConnection connection, long listId)
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
    /// True when global-proxy / all-UDP differ from the last loaded or committed values (#143).
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <inheritdoc />
    public event EventHandler? DirtyChanged;

    partial void OnAllUdpChanged(bool value)
    {
        OnEdited();
        FireAutoSave();
    }

    partial void OnUseGlobalProxyChanged(bool value)
    {
        OnEdited();
        FireAutoSave();
    }

    // The lifetime takes the same route as the toggles: an edit raises the confirm bar and the value leaves on
    // Save. It still applies live - the agent hands it to the running tunnel, so no reconnect is needed.
    partial void OnRouteTtlChanged(string value)
    {
        OnEdited();
        FireAutoSave();
    }

    /// <summary>
    /// Seeds the lifetime from the agent snapshot without pushing it back, leaving an uncommitted edit alone.
    /// </summary>
    public void ApplyRouteTtl(int seconds)
    {
        if (seconds < 0 || RouteTtl != _baseRouteTtl)
        {
            return;
        }

        var text = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _baseRouteTtl = text;
        if (RouteTtl == text)
        {
            return;
        }

        _loading = true;
        try
        {
            RouteTtl = text;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnEdited() => RecomputeDirty();

    private void RecomputeDirty()
    {
        if (_loading)
        {
            return;
        }

        // Any edit clears a stale validation / status line (#3).
        StatusMessage = string.Empty;

        var dirty = AllUdp != _baseAllUdp
            || UseGlobalProxy != _baseUseGlobalProxy
            || RouteTtl != _baseRouteTtl;
        if (dirty != IsDirty)
        {
            IsDirty = dirty;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public bool CanCommit() => !RouteTtlInvalid;

    /// <inheritdoc />
    public void CaptureBaseline()
    {
        _baseAllUdp = AllUdp;
        _baseUseGlobalProxy = UseGlobalProxy;
        _baseRouteTtl = RouteTtl;
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
            AllUdp = _baseAllUdp;
            UseGlobalProxy = _baseUseGlobalProxy;
            RouteTtl = _baseRouteTtl;
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
                AllUdp = root.TryGetProperty("allUdp", out var udp) && udp.ValueKind == JsonValueKind.True;
                UseGlobalProxy = root.TryGetProperty("useGlobalProxy", out var gp) && gp.ValueKind == JsonValueKind.True;
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
    /// Persists global-proxy + all-UDP for this list as one block (#143 header Save); applies on reconnect.
    /// Returns whether the agent accepted it. Exclusions are no longer edited here (bypass moved to the Direct
    /// bucket), so an empty exclusions arg is always sent.
    /// </summary>
    public async Task<bool> CommitAsync()
    {
        // An unparseable lifetime never leaves: the field is already marked, and a rejected block would look like
        // an agent failure instead of a typo.
        if (!TryParseTtl(RouteTtl, out var ttl))
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetRoutingSettings,
            [
                ListId.ToString(),
                string.Empty,
                AllUdp ? "on" : "off",
                UseGlobalProxy ? "full" : "split",
                UseGlobalProxy ? "on" : "off",
            ]));
            // Only a failure reason stays inline; a reconnect need shows via the standard banner (RestartRequired).
            StatusMessage = ack.Ok ? string.Empty : ack.Message;
            if (!ack.Ok)
            {
                return false;
            }

            // Agent-wide, and live: the running tunnel adopts it without a reconnect.
            if (RouteTtl == _baseRouteTtl)
            {
                return true;
            }

            var ttlAck = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting,
                [SettingKeys.RouteTtl, ttl.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
            StatusMessage = ttlAck.Ok ? string.Empty : ttlAck.Message;
            return ttlAck.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Fire-and-forget autosave for a toggle change (skipped while loading).
    private void FireAutoSave()
    {
        if (AutoSave && !_loading)
        {
            _ = AutoSaveAsync();
        }
    }

    /// <summary>
    /// Serialized autosave: persists the traffic block through the agent, re-running when an edit lands mid-commit.
    /// A draft whose list is not yet created (id 0) is skipped; it flushes once the list is saved and retargeted.
    /// </summary>
    public async Task AutoSaveAsync()
    {
        if (_loading || !AutoSave || ListId <= 0)
        {
            return;
        }

        if (_committing)
        {
            _commitPending = true;
            return;
        }

        _committing = true;
        try
        {
            do
            {
                _commitPending = false;
                if (!IsDirty)
                {
                    break;
                }

                if (await CommitAsync() && !_commitPending)
                {
                    CaptureBaseline();
                }
            }
            while (_commitPending);
        }
        finally
        {
            _committing = false;
        }
    }
}
