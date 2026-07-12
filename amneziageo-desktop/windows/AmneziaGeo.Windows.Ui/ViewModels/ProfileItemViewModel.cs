using System.Collections.ObjectModel;
using Avalonia.Media;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A single profile row in the list: it owns exactly one configuration plus its routing-list assignment
/// and connection state. Mutations are dispatched as IPC commands to the agent via the provided delegates.
/// </summary>
internal sealed partial class ProfileItemViewModel : ViewModelBase, IEditScope
{
    private readonly Func<string, string, Task<IpcAck>> _saveProfile;
    private readonly Func<string, long?, bool, Task<IpcAck>> _assignRouting;
    private readonly Func<string, Task> _selectProfile;
    private readonly Func<string, bool, Task> _setProfileConnection;
    private readonly Func<string, Task<IpcAck>> _removeConfig;
    private bool _suppress;
    private bool _suppressNextRoutingNone;
    private bool? _pendingRunning;

    // Baseline of the editable selections captured when the row is seeded / committed (#143); the profile is
    // dirty when the config / routing-list / use-routing selection differs from it.
    private string _baseConfigName = string.Empty;
    private long? _baseRoutingId;

    // Autosave: a config / routing pick persists at once; a reconnect need surfaces via the standard banner.
    private bool _committing;
    private bool _commitPending;

    /// <summary>
    /// When set, config / routing selections persist through the agent as they change (the open profile only).
    /// </summary>
    public bool AutoSave { get; set; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(HasConfig))]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    private string _config = string.Empty;

    [ObservableProperty]
    private ConfigChoice _selectedConfig = ConfigChoice.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private string _status = ConnectionStatus.Disconnected;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectActionText))]
    private bool _otherActive;

    [ObservableProperty]
    private RoutingListChoice _selectedRoutingList = RoutingListChoice.None;

    // Surfaces an agent rejection from the header Save (config/routing commit) so it is not silently lost (#143).
    [ObservableProperty]
    private string _editStatus = string.Empty;

    /// <summary>
    /// ctor
    /// </summary>
    public ProfileItemViewModel(
        Func<string, string, Task<IpcAck>> saveProfile,
        Func<string, long?, bool, Task<IpcAck>> assignRouting,
        Func<string, Task> selectProfile,
        Func<string, bool, Task> setProfileConnection,
        Func<string, Task<IpcAck>> removeConfig)
    {
        _saveProfile = saveProfile;
        _assignRouting = assignRouting;
        _selectProfile = selectProfile;
        _setProfileConnection = setProfileConnection;
        _removeConfig = removeConfig;
    }

    public ObservableCollection<ConfigChoice> ConfigOptions { get; } = [];

    /// <summary>
    /// The routing-list options available in the combo box.
    /// </summary>
    public ObservableCollection<RoutingListChoice> RoutingListOptions { get; } = [];

    /// <summary>
    /// The localized status label.
    /// </summary>
    public string StatusText => StatusLabels.Text(Status);

    /// <summary>
    /// The status badge color.
    /// </summary>
    public IBrush StatusBrush => StatusLabels.Brush(Status);

    /// <summary>
    /// Whether this profile is the one the agent currently has up. Drives the per-profile
    /// connect vs disconnect affordance.
    /// </summary>
    public bool IsRunning => _pendingRunning ?? !string.Equals(Status, ConnectionStatus.Disconnected, StringComparison.Ordinal);

    /// <summary>
    /// Label for the per-profile connect button: "Переключить" when a DIFFERENT profile is the live tunnel
    /// (tapping switches to this one), otherwise "Подключить".
    /// </summary>
    public string ConnectActionText => OtherActive ? Loc.Instance.Get("Profile_SwitchAction") : Loc.Instance.Get("Profile_ConnectAction");

    /// <summary>
    /// Whether the profile has a configuration assigned.
    /// </summary>
    public bool HasConfig => Config.Length > 0;

    public bool IsComplete => HasConfig;

    /// <summary>
    /// Collapsed-row summary: the configuration name, or a hint when none is set yet.
    /// </summary>
    public string Detail => HasConfig ? Config : Loc.Instance.Get("Profile_NoConfig");

    /// <summary>
    /// Re-raises the localized computed labels after a language change.
    /// </summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ConnectActionText));
        OnPropertyChanged(nameof(Detail));
    }

    [RelayCommand]
    private Task Select()
    {
        return _selectProfile(Name);
    }

    [RelayCommand]
    private Task ConnectProfile()
    {
        SetPendingRunning(true);
        return _setProfileConnection(Name, true);
    }

    [RelayCommand]
    private Task DisconnectProfile()
    {
        SetPendingRunning(false);
        return _setProfileConnection(Name, false);
    }

    private void SetPendingRunning(bool value)
    {
        _pendingRunning = value;
        OnPropertyChanged(nameof(IsRunning));
    }

    /// <summary>
    /// Populates view-model state from the agent-side entry without firing change-driven IPC calls.
    /// </summary>
    public void ApplyFromEntry(
        ProfileEntry entry,
        IReadOnlyList<RoutingListChoice> routingOptions,
        IReadOnlyList<ConfigChoice> configOptions)
    {
        _suppress = true;
        try
        {
            Name = entry.Name;
            Status = entry.Status;

            // While the user is mid-edit on this profile (#143), do NOT reseed the edited selections from the
            // snapshot - that would wipe the pending config / routing change. Name + Status still update.
            if (!IsDirty)
            {
                Config = entry.Config;

                // Keep the assigned config selectable even if it's not in the catalogue.
                var effectiveOptions = configOptions;
                if (entry.Config.Length > 0
                    && !configOptions.Any(option => option.IsReal && string.Equals(option.Name, entry.Config, StringComparison.Ordinal)))
                {
                    var augmented = configOptions.ToList();
                    augmented.Add(new ConfigChoice(entry.Config));
                    effectiveOptions = augmented;
                }

                if (!ConfigOptions.SequenceEqual(effectiveOptions))
                {
                    ConfigOptions.Clear();
                    foreach (var option in effectiveOptions)
                    {
                        ConfigOptions.Add(option);
                    }
                }

                SelectedConfig = ConfigOptions.FirstOrDefault(
                        option => option.IsReal && string.Equals(option.Name, entry.Config, StringComparison.Ordinal))
                    ?? ConfigChoice.None;

                if (!RoutingListOptions.SequenceEqual(routingOptions))
                {
                    // Arm the one-shot flag before Clear() fires a None echo.
                    _suppressNextRoutingNone = SelectedRoutingList.IsReal;
                    RoutingListOptions.Clear();
                    foreach (var option in routingOptions)
                    {
                        RoutingListOptions.Add(option);
                    }
                }

                // A routing list is shown as selected only when it is actually in use: selecting a list now
                // implies use_routing on, and «Полный туннель» (none) implies off - the separate toggle is gone.
                SelectedRoutingList = entry.UseRouting
                    ? RoutingListOptions.FirstOrDefault(option => option.Id == entry.RoutingListId) ?? RoutingListChoice.None
                    : RoutingListChoice.None;
            }
        }
        finally
        {
            _suppress = false;
        }

        // The one-shot routing-None echo guard is only meaningful during the suppressed reseed above (where the
        // _suppress guard already absorbs the Clear() echo); clear any leftover arming so a catalogue change with
        // no realized combo can't leave it armed and later swallow a genuine «Полный туннель» pick (#143 review).
        _suppressNextRoutingNone = false;

        // Seeded from the snapshot (not mid-edit): this backend state is the clean baseline (#143).
        if (!IsDirty)
        {
            CaptureBaseline();
        }

        // Clear the optimistic status once the real status matches.
        var statusRunning = !string.Equals(Status, ConnectionStatus.Disconnected, StringComparison.Ordinal);
        if (_pendingRunning == statusRunning)
        {
            _pendingRunning = null;
        }

        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(IsRunning));
    }

    /// <summary>
    /// Remove the config from the catalogue and clear it from this profile.
    /// </summary>
    public async Task<IpcAck> DeleteConfigAsync(string config)
    {
        var ack = await _removeConfig(config);
        if (!ack.Ok)
        {
            return ack;
        }

        Config = string.Empty;
        await _saveProfile(Name, string.Empty);
        return ack;
    }

    partial void OnSelectedConfigChanged(ConfigChoice? oldValue, ConfigChoice newValue)
    {
        // newValue can be null while the options are being cleared.
        if (_suppress || newValue is null)
        {
            return;
        }

        MarkDirty();
        FireAutoSave();
    }

    partial void OnSelectedRoutingListChanged(RoutingListChoice? oldValue, RoutingListChoice newValue)
    {
        // newValue can be null while the options are being cleared.
        if (_suppress || newValue is null)
        {
            return;
        }

        // Swallow the Clear() echo in the routing combo.
        if (!newValue.IsReal && _suppressNextRoutingNone)
        {
            _suppressNextRoutingNone = false;
            return;
        }
        _suppressNextRoutingNone = false;

        MarkDirty();
        FireAutoSave();
    }

    // Fire-and-forget autosave for a config / routing pick (skipped while the row is being reseeded).
    private void FireAutoSave()
    {
        if (AutoSave && !_suppress)
        {
            _ = AutoSaveAsync();
        }
    }

    /// <summary>
    /// Serialized autosave: persists the config / routing change through the agent, re-running when a pick lands
    /// mid-commit.
    /// </summary>
    public async Task AutoSaveAsync()
    {
        if (_suppress || !AutoSave)
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

    // ---- IEditScope (#143): config / routing-list / use-routing edits are held in the buffer and committed
    // together by the header Save (config via OpAddProfile, routing via OpAssignRouting), reverted by Cancel. ----

    /// <inheritdoc />
    public bool IsDirty { get; private set; }

    /// <inheritdoc />
    public event EventHandler? DirtyChanged;

    private void MarkDirty()
    {
        if (_suppress)
        {
            return;
        }

        var dirty = !string.Equals(CurrentConfigName(), _baseConfigName, StringComparison.Ordinal)
            || CurrentRoutingId() != _baseRoutingId;
        if (dirty != IsDirty)
        {
            IsDirty = dirty;
            if (!dirty)
            {
                // Returned to baseline (via Cancel or by re-selecting the original): drop any stale commit error.
                EditStatus = string.Empty;
            }

            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string CurrentConfigName() => SelectedConfig is { IsReal: true } config ? config.Name : string.Empty;

    private long? CurrentRoutingId() => SelectedRoutingList is { IsReal: true } list ? list.Id : null;

    /// <inheritdoc />
    public bool CanCommit() => true;

    /// <inheritdoc />
    public void CaptureBaseline()
    {
        _baseConfigName = CurrentConfigName();
        _baseRoutingId = CurrentRoutingId();
        if (IsDirty)
        {
            IsDirty = false;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Revert()
    {
        _suppress = true;
        try
        {
            SelectedConfig = ConfigOptions.FirstOrDefault(option => option.IsReal && string.Equals(option.Name, _baseConfigName, StringComparison.Ordinal)) ?? ConfigChoice.None;
            SelectedRoutingList = _baseRoutingId is { } id
                ? RoutingListOptions.FirstOrDefault(option => option.Id == id) ?? RoutingListChoice.None
                : RoutingListChoice.None;
            EditStatus = string.Empty;
        }
        finally
        {
            _suppress = false;
        }

        MarkDirty();
    }

    /// <inheritdoc />
    public async Task<bool> CommitAsync()
    {
        var configName = CurrentConfigName();
        if (!string.Equals(configName, _baseConfigName, StringComparison.Ordinal))
        {
            Config = configName;
            var ack = await _saveProfile(Name, configName);
            if (!ack.Ok)
            {
                EditStatus = ack.Message;
                return false;
            }
        }

        var routingId = CurrentRoutingId();
        if (routingId != _baseRoutingId)
        {
            // Selecting a list implies use_routing on; «Полный туннель» (none) implies off - the toggle is gone.
            var ack = await _assignRouting(Name, routingId, routingId is not null);
            if (!ack.Ok)
            {
                EditStatus = ack.Message;
                return false;
            }
        }

        EditStatus = string.Empty;
        return true;
    }
}
