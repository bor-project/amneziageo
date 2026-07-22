using System.Collections.ObjectModel;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Profile screen: the top Profile / Import / Export menu, the profile catalogue (profile = config × routing),
/// the open-profile editor (rename / config picker / routing picker / delete), profile creation, and the reused
/// bundle import / export. The connection card's active-profile selection, the config catalogue, and the
/// shared-namespace name check live on the shell, reached through <c>_host</c>.
/// </summary>
internal sealed partial class ProfileViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly IAgentConnection _connection;

    private string? _pendingOpenProfile;
    private ProfileItemViewModel? _adoptTarget;
    private string? _pendingAdoptConfig;
    private long? _pendingAdoptRoutingList;
    private bool _suppressOpenChoice;

    // Rename baseline: the open profile's persisted name; a differing ProfileRename saves on the section Save.
    private string _baseProfileName = string.Empty;

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    private bool _isCompact;

    // Whether this section is the one currently shown, pushed by the shell; gates the footer Save bar.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    private bool _isActiveSection;

    // Top menu section: Profile (edit) / Import (bundle) / Export (bundle).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSectionProfile))]
    [NotifyPropertyChangedFor(nameof(IsSectionImport))]
    [NotifyPropertyChangedFor(nameof(IsSectionExport))]
    [NotifyPropertyChangedFor(nameof(SegProfileActive))]
    [NotifyPropertyChangedFor(nameof(SegImportActive))]
    [NotifyPropertyChangedFor(nameof(SegExportActive))]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    private ProfileScreenSection _screenSection = ProfileScreenSection.Profile;

    [ObservableProperty]
    private BundleImportViewModel? _bundleImport;

    [ObservableProperty]
    private BundleExportViewModel? _bundleExport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileDetail))]
    private ProfileItemViewModel? _openProfile;

    [ObservableProperty]
    private ProfileChoice? _openProfileChoice = ProfileChoice.None;

    [ObservableProperty]
    private bool _profileDeletePending;

    [ObservableProperty]
    private string _profileDeleteStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileNameMissing))]
    private string _profileRename = string.Empty;

    [ObservableProperty]
    private string _profileRenameStatus = string.Empty;

    /// <summary>
    /// ctor
    /// </summary>
    public ProfileViewModel(MainWindowViewModel host, IAgentConnection connection)
    {
        _host = host;
        _connection = connection;
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged()
    {
        foreach (var profile in Profiles)
        {
            profile.RefreshLocalizedLabels();
        }
    }

    /// <summary>
    /// Profile rows (profile = config × routing).
    /// </summary>
    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public ObservableCollection<ProfileChoice> ProfileOptions { get; } = [ProfileChoice.None];

    public bool IsProfileDetail => OpenProfile is not null;

    public bool ProfileNameMissing => string.IsNullOrWhiteSpace(ProfileRename);

    /// <summary>
    /// Whether the config catalogue is non-empty, surfaced for this screen's hints and the add button.
    /// </summary>
    public bool HasConfigs => _host.HasConfigs;

    public bool ShowNoProfilesYetHint => _host.ShowNoProfilesYetHint;

    // ---- Top menu sections (Profile / Import / Export), mirroring the Config screen. ----

    public bool IsSectionProfile => ScreenSection == ProfileScreenSection.Profile;

    public bool IsSectionImport => ScreenSection == ProfileScreenSection.Import;

    public bool IsSectionExport => ScreenSection == ProfileScreenSection.Export;

    public bool SegProfileActive => ScreenSection == ProfileScreenSection.Profile;

    public bool SegImportActive => ScreenSection == ProfileScreenSection.Import;

    public bool SegExportActive => ScreenSection == ProfileScreenSection.Export;

    // ---- Footer Save/Cancel bar: the open-profile edits (rename / config / routing) are held and committed
    // atomically on the footer Save, reverted on Cancel. Only the Profile section is editable; Import / Export
    // are self-contained bundle tools with their own buttons. ----

    // The open profile's rename differs from its persisted name.
    private bool RenameDirty => !string.Equals(ProfileRename ?? string.Empty, _baseProfileName, StringComparison.Ordinal);

    /// <summary>
    /// Whether the open-profile editors hold uncommitted changes (config / routing picks or the rename).
    /// </summary>
    public bool IsEditDirty => (OpenProfile?.IsDirty ?? false) || RenameDirty;

    /// <summary>
    /// Whether the footer Save/Cancel bar is shown: dirty open-profile edits while the Profile section is on screen.
    /// </summary>
    public bool ShowSaveBar => IsActiveSection && IsSectionProfile && IsEditDirty;

    /// <summary>
    /// Whether the footer Save button is shown.
    /// </summary>
    public bool ShowSaveButton => true;

    /// <summary>
    /// Whether the footer Save button is enabled.
    /// </summary>
    public bool CanSave => IsEditDirty;

    private void RefreshEditBar()
    {
        OnPropertyChanged(nameof(IsEditDirty));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(ShowSaveButton));
        OnPropertyChanged(nameof(CanSave));
    }

    private void OnEditScopeDirty(object? sender, EventArgs e) => RefreshEditBar();

    // Re-raise the host-derived flags after the shell recomputes them on a snapshot.
    public void NotifyHostFlagsChanged()
    {
        OnPropertyChanged(nameof(HasConfigs));
        OnPropertyChanged(nameof(ShowNoProfilesYetHint));
        CreateProfileCommand.NotifyCanExecuteChanged();
    }

    // Re-gate the "Set active" button after the connection card's active profile changes.
    public void NotifyActiveProfileChanged()
    {
        SetActiveProfileCommand.NotifyCanExecuteChanged();
    }

    // The combo-selected profile can be set active unless it already is.
    private bool CanSetActiveProfile =>
        OpenProfile is { } profile
        && !string.Equals(profile.Name, _host.Home.ActiveProfile?.Name, StringComparison.Ordinal);

    // Makes the combo-selected profile the active one (agent target + launcher selection), without connecting.
    [RelayCommand(CanExecute = nameof(CanSetActiveProfile))]
    private void SetActiveProfile()
    {
        if (OpenProfile is not null)
        {
            _host.Home.ActiveProfile = OpenProfile;
        }
    }

    // Top menu: Profile / Import / Export. Import / Export open the reused bundle tools; Profile lands on the
    // open (active / first) profile's editor.
    [RelayCommand]
    private void SelectProfileSection(string target)
    {
        if (target == "import")
        {
            BundleImport ??= new BundleImportViewModel(_connection);
            ScreenSection = ProfileScreenSection.Import;
            return;
        }

        if (target == "export")
        {
            var export = new BundleExportViewModel(_connection, Profiles, _host.Config.Configs, _host.Routing.RoutingLists);
            BundleExport = export;
            ScreenSection = ProfileScreenSection.Export;
            _ = export.LoadRoutingRulesAsync();
            return;
        }

        ScreenSection = ProfileScreenSection.Profile;
        EnterSection();
    }

    // Entering the profile section: land on the Profile tab and open the active profile, or the first one, so it
    // never opens empty (#28).
    public void EnterSection()
    {
        ScreenSection = ProfileScreenSection.Profile;
        if (OpenProfile is not null)
        {
            return;
        }

        var target = PreferredProfile();
        if (target is not null)
        {
            OpenProfile = target;
        }
    }

    // The active profile when it is one of our rows, else the first in the catalogue.
    private ProfileItemViewModel? PreferredProfile()
    {
        var active = _host.Home.ActiveProfile;
        if (active is not null)
        {
            var match = Profiles.FirstOrDefault(p => string.Equals(p.Name, active.Name, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        return Profiles.FirstOrDefault();
    }

    // Footer Save: commit the open profile's config / routing picks then its rename atomically. A rejected step
    // surfaces its own reason and leaves the rest pending. Order: rename last so the config / routing ops key by
    // the old name.
    [RelayCommand]
    private async Task SaveSection()
    {
        if (OpenProfile is null)
        {
            return;
        }

        if (OpenProfile is { IsDirty: true } profile)
        {
            if (!await profile.CommitAsync())
            {
                RefreshEditBar();
                return;
            }

            profile.CaptureBaseline();
        }

        if (RenameDirty && !await CommitProfileRenameAsync())
        {
            RefreshEditBar();
            return;
        }

        RefreshEditBar();
    }

    // Footer Cancel: revert the config / routing picks and the rename to their loaded baseline.
    [RelayCommand]
    private void CancelSection()
    {
        OpenProfile?.Revert();
        ProfileRename = _baseProfileName;
        ProfileRenameStatus = string.Empty;
        RefreshEditBar();
    }

    /// <summary>
    /// Reconciles the profile catalogue from the snapshot, then re-opens a just-created profile.
    /// </summary>
    public void Apply(IReadOnlyList<ProfileEntry> entries, IReadOnlyList<RoutingListEntry> routingLists)
    {
        var options = BuildRoutingOptions(routingLists);
        var configOptions = _host.Config.BuildConfigOptions();

        // Reconcile in place, matching rows by name, so transient view state (the expanded
        // editor, combo selection) survives the snapshot pushes that follow every edit.
        var present = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = Profiles.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(Profiles[i].Name))
            {
                Profiles.RemoveAt(i);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = Profiles.FirstOrDefault(b => string.Equals(b.Name, entry.Name, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new ProfileItemViewModel(SaveProfileAsync, AssignRoutingAsync, _host.Home.SelectProfileAsync, _host.Home.ToggleProfileConnectionAsync, _host.Config.RemoveConfigAsync);
                existing.ApplyFromEntry(entry, options, configOptions);
                Profiles.Insert(Math.Min(i, Profiles.Count), existing);
                continue;
            }

            existing.ApplyFromEntry(entry, options, configOptions);
            var index = Profiles.IndexOf(existing);
            if (index != i)
            {
                Profiles.Move(index, i);
            }
        }

        // Keep the shared combo options in step with the profile rows (reconciled in place so neither combo's
        // selection is nulled), then re-mirror both selections onto the refreshed option instances.
        ReconcileProfileOptions();

        // If the profile opened in the detail view was removed elsewhere, fall back to the list.
        if (OpenProfile is not null && !Profiles.Contains(OpenProfile))
        {
            OpenProfile = null;
        }

        // Open a profile just created via "+ Профиль" straight into its editor so a config/routing can be
        // picked immediately.
        if (_pendingOpenProfile is not null)
        {
            var created = Profiles.FirstOrDefault(b => string.Equals(b.Name, _pendingOpenProfile, StringComparison.Ordinal));
            if (created is not null)
            {
                OpenProfile = created;
                _pendingOpenProfile = null;
            }
        }

        AdoptPending();

        _host.Home.SyncActiveProfileChoice();
        SyncOpenProfileChoice();
    }

    /// <summary>
    /// Selects a just-added config in the open profile once its row lands in the next snapshot.
    /// </summary>
    internal void AdoptConfig(string name)
    {
        if (OpenProfile is { } profile)
        {
            _adoptTarget = profile;
            _pendingAdoptConfig = name;
        }
    }

    /// <summary>
    /// Selects a just-created routing list in the open profile once its row lands in the next snapshot.
    /// </summary>
    internal void AdoptRoutingList(long id)
    {
        if (OpenProfile is { } profile)
        {
            _adoptTarget = profile;
            _pendingAdoptRoutingList = id;
        }
    }

    // Picks a pending config / routing list in the profile it was armed for, staging it like a manual pick.
    // Applies at most once - a differing open profile drops it, an unresolved option is not retried.
    private void AdoptPending()
    {
        if (_pendingAdoptConfig is null && _pendingAdoptRoutingList is null)
        {
            return;
        }

        if (!ReferenceEquals(OpenProfile, _adoptTarget) || _adoptTarget is not { } profile)
        {
            _adoptTarget = null;
            _pendingAdoptConfig = null;
            _pendingAdoptRoutingList = null;
            return;
        }

        if (_pendingAdoptConfig is { } configName)
        {
            _pendingAdoptConfig = null;
            if (profile.ConfigOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Name, configName, StringComparison.Ordinal)) is { } config)
            {
                profile.SelectedConfig = config;
            }
        }

        if (_pendingAdoptRoutingList is { } listId)
        {
            _pendingAdoptRoutingList = null;
            if (profile.RoutingListOptions.FirstOrDefault(o => o.IsReal && o.Id == listId) is { } list)
            {
                profile.SelectedRoutingList = list;
            }
        }

        _adoptTarget = null;
    }

    /// <summary>
    /// Tears down the profile catalogue on disconnect, leaving the combos on «— не выбрано —».
    /// </summary>
    public void Reset()
    {
        Profiles.Clear();
        OpenProfile = null;
        _adoptTarget = null;
        _pendingAdoptConfig = null;
        _pendingAdoptRoutingList = null;
        _baseProfileName = string.Empty;
        ScreenSection = ProfileScreenSection.Profile;
        BundleImport = null;
        BundleExport = null;
        ReconcileProfileOptions();
    }

    // Track the open profile so its SelectedRoutingList drives the inline rule editor. Subscribing to the
    // instance is safe because the snapshot reconcile keeps the same ProfileItemViewModel instances.
    partial void OnOpenProfileChanged(ProfileItemViewModel? oldValue, ProfileItemViewModel? newValue)
    {
        // Leaving the profile disarms any pending delete confirmation (#143).
        ProfileDeletePending = false;
        ProfileDeleteStatus = string.Empty;

        // Open the profile's single configuration so its editors (text / proxy) are available on the rail.
        _host.Config.OpenConfig = string.IsNullOrEmpty(newValue?.Config) ? null : newValue!.Config;

        // Reset the rename field only when the profile identity actually changes. A background snapshot can
        // re-assign the open profile to a refreshed instance of the SAME profile; without this guard that
        // would wipe the half-typed name mid-edit.
        var sameProfile = oldValue is not null && newValue is not null
            && string.Equals(oldValue.Name, newValue.Name, StringComparison.Ordinal);
        if (!sameProfile)
        {
            // Leaving a profile with staged (unsaved) config / routing picks discards them, so a stale dirty row
            // does not linger and freeze its snapshot reseed. Only on a real identity change.
            if (oldValue is { IsDirty: true })
            {
                oldValue.Revert();
            }

            // Discard any live rename preview on the profile we are leaving: snap its combo label back to the
            // persisted name so an unsaved (or agent-rejected) edit does not linger in the picker (#110). Only
            // on a real identity change - a background re-bind of the same profile keeps the in-progress name.
            if (oldValue is not null)
            {
                ResetProfileOptionLabel(oldValue.Name);
            }

            // Set the rename baseline before the field so seeding it does not read as a dirty edit.
            _baseProfileName = newValue?.Name ?? string.Empty;
            ProfileRename = newValue?.Name ?? string.Empty;
            ProfileRenameStatus = string.Empty;
            // Reflect the profile's routing list into the Routing section too (mirrors OpenConfig above), so
            // opening a profile - or gearing into Settings on the active one - lands the Routing section on the
            // list this profile actually uses. Guarded to real identity changes so a background snapshot re-bind
            // of the SAME profile does not yank a routing list the user is editing.
            _host.Routing.OpenForProfile(newValue);
        }

        // Only the open profile's edit-scope drives the footer Save bar; the other catalogue rows stay passive.
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnOpenProfilePropertyChanged;
            oldValue.DirtyChanged -= OnEditScopeDirty;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnOpenProfilePropertyChanged;
            newValue.DirtyChanged += OnEditScopeDirty;
        }

        // Mirror the open profile into the Profile-section combo so it shows «— не выбрано —» / the name.
        SyncOpenProfileChoice();
        SetActiveProfileCommand.NotifyCanExecuteChanged();
        RefreshEditBar();
    }

    private void OnOpenProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileItemViewModel.Config))
        {
            _host.Config.OpenConfig = string.IsNullOrEmpty(OpenProfile?.Config) ? null : OpenProfile!.Config;
        }
    }

    partial void OnOpenProfileChoiceChanged(ProfileChoice? value)
    {
        if (_suppressOpenChoice || value is null)
        {
            return;
        }

        OpenProfile = value.IsReal
            ? Profiles.FirstOrDefault(b => string.Equals(b.Name, value.Identity, StringComparison.Ordinal))
            : null;
    }

    // The rename field changed: clear a stale validation line (#3) and refresh the Save bar. It commits on the
    // footer Save, not per keystroke.
    partial void OnProfileRenameChanged(string value)
    {
        ProfileRenameStatus = string.Empty;
        RefreshEditBar();
    }

    private void SyncOpenProfileChoice()
    {
        _suppressOpenChoice = true;
        OpenProfileChoice = OpenProfile is null
            ? ProfileChoice.None
            : ProfileOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Identity, OpenProfile.Name, StringComparison.Ordinal)) ?? ProfileChoice.None;
        _suppressOpenChoice = false;
    }

    // Reset the option label back to the persisted name.
    private void ResetProfileOptionLabel(string identity)
    {
        var option = ProfileOptions.FirstOrDefault(
            o => o.IsReal && string.Equals(o.Identity, identity, StringComparison.Ordinal));
        if (option is not null && !string.Equals(option.Name, option.Identity, StringComparison.Ordinal))
        {
            option.Name = option.Identity;
        }
    }

    // Reconcile ProfileOptions in place from Profiles: keep «— не выбрано —» at [0] and reconcile the real
    // choices after it - dropping removed profiles, adding new ones, and reordering to match. Options are
    // matched by Identity (the persisted profile name), which stays stable across a live-typed rename, so an
    // in-progress rename preview (#110) is not clobbered by a snapshot push. Editing in place (rather than
    // Clear + rebuild) keeps the None / existing choice instances alive so a bound ComboBox's selection is not
    // reset by the refresh.
    internal void ReconcileProfileOptions()
    {
        const int head = 1; // None occupies [0].
        var present = Profiles.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = ProfileOptions.Count - 1; i >= head; i--)
        {
            if (!present.Contains(ProfileOptions[i].Identity))
            {
                ProfileOptions.RemoveAt(i);
            }
        }

        for (var i = 0; i < Profiles.Count; i++)
        {
            var name = Profiles[i].Name;
            var slot = head + i;
            var existing = ProfileOptions.Skip(head).FirstOrDefault(o => string.Equals(o.Identity, name, StringComparison.Ordinal));
            if (existing is null)
            {
                ProfileOptions.Insert(Math.Min(slot, ProfileOptions.Count), new ProfileChoice(name));
                continue;
            }

            var index = ProfileOptions.IndexOf(existing);
            if (index != slot)
            {
                ProfileOptions.Move(index, slot);
            }
        }
    }

    private static IReadOnlyList<RoutingListChoice> BuildRoutingOptions(IReadOnlyList<RoutingListEntry> entries)
    {
        // Order: «Полный туннель» (None) then the saved lists. Creating a list is the «+ Новый список» button
        // in the Routing section, not a combo entry (#111).
        var options = new List<RoutingListChoice> { RoutingListChoice.None };
        foreach (var entry in entries)
        {
            options.Add(new RoutingListChoice(entry.Id, entry.Name));
        }

        return options;
    }

    // The first profile instance that is not the one just deleted, or null when it was the last one.
    private ProfileItemViewModel? NextProfileAfter(ProfileItemViewModel deleted) =>
        Profiles.FirstOrDefault(b => !ReferenceEquals(b, deleted));

    private async Task<IpcAck> SaveProfileAsync(string name, string config)
    {
        return await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAddProfile, [name, config]));
    }

    private async Task<IpcAck> AssignRoutingAsync(string profile, long? listId, bool useRouting)
    {
        var args = new[]
        {
            profile,
            listId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            useRouting ? "on" : "off",
        };
        return await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAssignRouting, args));
    }

    // "+ Профиль": create a profile pre-assigned a configuration (#113), then auto-open it so it can be tuned.
    // Disabled while the catalogue is empty (a profile needs a config to dial). There is always a config to
    // assign here: prefer the current working profile's config, else the first in the catalogue.
    private bool CanCreateProfile => _host.HasConfigs;

    [RelayCommand(CanExecute = nameof(CanCreateProfile))]
    private async Task CreateProfile()
    {
        var config = _host.Home.ActiveProfile is { Config.Length: > 0 } active
            ? active.Config
            : _host.Config.ConfigNames.FirstOrDefault() ?? string.Empty;

        var name = UniqueProfileName();
        string[] args = config.Length > 0 ? [name, config] : [name];
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAddProfile, args));
        if (ack.Ok)
        {
            _pendingOpenProfile = name;
        }
    }

    private string UniqueProfileName()
    {
        var baseName = Loc.Instance.Get("MainVm_NewProfileDefaultName");
        var existing = Profiles.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    // The profile Delete trigger (#4/#6): a profile that is currently connected (in use) cannot be deleted -
    // surface why instead of arming. Otherwise arm the inline confirm/cancel pair that replaces the Delete
    // button in place.
    [RelayCommand]
    private void RequestDeleteOpenProfile()
    {
        if (OpenProfile is null)
        {
            return;
        }

        ProfileDeleteStatus = string.Empty;
        if (OpenProfile.IsRunning)
        {
            ProfileDeleteStatus = Loc.Instance.Get("Main_ProfileInUse");
            return;
        }

        ProfileDeletePending = true;
    }

    // Inline Cancel: disarm the profile delete confirm.
    [RelayCommand]
    private void CancelDeleteProfile()
    {
        ProfileDeletePending = false;
        ProfileDeleteStatus = string.Empty;
    }

    // Inline Confirm: delete the profile. The in-use guard is re-checked (the profile may have been connected
    // since the confirm was armed). On success the next remaining profile opens (or the list, when it was the
    // last); a rejected delete keeps the view put and shows why.
    [RelayCommand]
    private async Task ConfirmDeleteOpenProfile()
    {
        ProfileDeletePending = false;
        if (OpenProfile is null)
        {
            return;
        }

        var profile = OpenProfile;
        if (profile.IsRunning)
        {
            ProfileDeleteStatus = Loc.Instance.Get("Main_ProfileInUse");
            return;
        }

        var ack = await _connection.SendCommandAsync(
            new IpcCommand(IpcContract.OpRemoveProfile, [profile.Name]));
        if (!ack.Ok)
        {
            ProfileDeleteStatus = ack.Message;
            return;
        }

        OpenProfile = NextProfileAfter(profile);
    }

    // Commit the open profile's rename through the agent. On OK the live instance adopts the
    // new name so the next snapshot reconciles it in place (Apply matches by name) instead of dropping the row.
    // An empty name is rejected (the item stays dirty); a refused rename shows why and reverts the combo label
    // to the persisted name.
    private async Task<bool> CommitProfileRenameAsync()
    {
        var profile = OpenProfile;
        if (profile is null)
        {
            return true;
        }

        var next = (ProfileRename ?? string.Empty).Trim();
        if (next.Length == 0)
        {
            ProfileRenameStatus = Loc.Instance.Get("Main_RequiredEmptyWarning");
            return false;
        }

        if (string.Equals(next, profile.Name, StringComparison.Ordinal))
        {
            _baseProfileName = next;
            return true;
        }

        ProfileRenameStatus = string.Empty;
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRenameProfile, [profile.Name, next]));
        if (ack.Ok)
        {
            profile.Name = next;
            _baseProfileName = next;
            return true;
        }

        ProfileRenameStatus = ack.Message;
        ResetProfileOptionLabel(profile.Name);
        return false;
    }
}

/// <summary>
/// Profile screen top-menu section.
/// </summary>
internal enum ProfileScreenSection
{
    Profile,
    Import,
    Export,
}
