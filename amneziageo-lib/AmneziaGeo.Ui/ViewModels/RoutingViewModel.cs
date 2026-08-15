using System.Collections.ObjectModel;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Routing screen: the routing-list catalogue, the top Settings / Import / Export menu, the rule/per-routing
/// editors, the import create-form, and list CRUD. The shared catalogue lives on the shell, reached through
/// <c>_host</c>.
/// </summary>
internal sealed partial class RoutingViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly IAgentConnection _connection;
    private readonly UiPreferences _prefs;

    private long? _pendingEditRoutingListId;
    private bool _suppressCatalogueRouting;

    // Set once the agent has reported its catalogue; until then an empty one only means "not loaded".
    private bool _catalogueKnown;

    // Set while the section waits for that first report.
    private bool _enterDeferred;
    // The list open before "+ Импорт" so Cancel restores it.
    private RoutingListSummaryViewModel? _listBeforeCreate;

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompactActive))]
    private bool _isCompact;

    // Whether this section is the one currently shown, pushed by the shell; gates the footer Save bar.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    private bool _isActiveSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoutingEditor))]
    private RoutingListEditorViewModel? _routingEditor;

    [ObservableProperty]
    private RoutingSettingsViewModel? _routingSettings;

    [ObservableProperty]
    private bool _sectionLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenListActive))]
    [NotifyPropertyChangedFor(nameof(UseOpenList))]
    [NotifyPropertyChangedFor(nameof(DeleteListPrompt))]
    private RoutingListSummaryViewModel? _editRoutingList;

    // The routing list every config uses, mirrored from the snapshot; null leaves each config on its own settings.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenListActive))]
    [NotifyPropertyChangedFor(nameof(UseOpenList))]
    private long? _selectedRoutingListId;

    [ObservableProperty]
    private RoutingListChoice? _selectedCatalogueRouting = RoutingListChoice.None;

    // The list every config routes by, as the server screen picks it; a pick is applied at once.
    [ObservableProperty]
    private RoutingListChoice? _homeRoutingChoice = RoutingListChoice.None;

    private bool _suppressHomeRouting;

    partial void OnHomeRoutingChoiceChanged(RoutingListChoice? value)
    {
        if (_suppressHomeRouting || value is null || value.Id == SelectedRoutingListId)
        {
            return;
        }

        _ = AssignRoutingAsync(value.Id);
    }

    [ObservableProperty]
    private bool _routingDeletePending;

    [ObservableProperty]
    private string _routingDeleteStatus = string.Empty;

    [ObservableProperty]
    private bool _hasRoutingLists;

    // Manage sub-section shown by the top menu (Settings vs Export). Import is IsCreatingSectionRouting.
    [ObservableProperty]
    private RoutingSection _manageSection = RoutingSection.Settings;

    // True while the "+ Импорт" create draft is open.
    [ObservableProperty]
    private bool _isCreatingSectionRouting;

    // Import create-form method (picker / manual editor / live scanner).
    [ObservableProperty]
    private RoutingImportMethod _importMethod = RoutingImportMethod.Picker;

    // Live QR scanner for the import form; non-null only while the camera method is active.
    [ObservableProperty]
    private ScanViewModel? _sectionScan;

    /// <summary>
    /// Routing-list catalogue.
    /// </summary>
    public ObservableCollection<RoutingListSummaryViewModel> RoutingLists { get; } = [];

    public ObservableCollection<RoutingListChoice> RoutingCatalogueOptions { get; } = [];

    /// <summary>
    /// The same catalogue behind the none choice: what the server screen offers as the list to route by.
    /// </summary>
    public ObservableCollection<RoutingListChoice> HomeRoutingOptions { get; } = [RoutingListChoice.None];

    /// <summary>
    /// Имена сохранённых списков маршрутизации.
    /// </summary>
    public IReadOnlyList<string> ListNames => RoutingLists.Select(r => r.Name).ToList();

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingViewModel(MainWindowViewModel host, IAgentConnection connection, UiPreferences prefs)
    {
        _host = host;
        _connection = connection;
        _prefs = prefs;
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged()
    {
        foreach (var list in RoutingLists)
        {
            list.RefreshLocalizedLabels();
        }

        RoutingEditor?.RefreshLocalizedLabels();
        OnPropertyChanged(nameof(DeleteListPrompt));
    }

    /// <summary>
    /// Набор способов оболочки: шторка «Добавить» / «Экспорт».
    /// </summary>
    public ActionSheetViewModel Sheet => _host.Sheet;

    /// <summary>
    /// Whether a rule editor exists (a list is selected or a new draft is open).
    /// </summary>
    public bool HasRoutingEditor => RoutingEditor is not null;

    // ---- Top menu sections (Settings / Import / Export), mirroring the Config screen. ----

    public bool IsSectionImport => IsCreatingSectionRouting;

    public bool IsSectionSettings => !IsCreatingSectionRouting && RoutingEditor is not null && ManageSection == RoutingSection.Settings;

    public bool IsSectionExport => !IsCreatingSectionRouting && RoutingEditor is not null && ManageSection == RoutingSection.Export;

    /// <summary>
    /// Стоит ли тумблер применения в шапке: на узком экране карточка под выбором стоила пол-экрана, и её
    /// содержимое ужато в строку у самого выбора.
    /// </summary>
    public bool ShowCompactActive => IsCompact && IsSectionSettings;

    /// <summary>
    /// Стоит ли на экране выбор списка с кнопками «Добавить» и «Экспорт»: черновик, сканер и экспорт занимают
    /// экран целиком и возвращают сюда сами.
    /// </summary>
    public bool ShowHeaderActions => !IsCreatingSectionRouting && !IsSectionExport && !ShowCatalogueLoader;

    /// <summary>
    /// Delete-card prompt naming the open list.
    /// </summary>
    public string DeleteListPrompt => Loc.Instance.Get("Main_DeleteListPrompt", EditRoutingList?.Name ?? string.Empty);

    /// <summary>
    /// Есть ли что экспортировать.
    /// </summary>
    public bool CanExportOpenList => !IsCreatingSectionRouting && RoutingEditor is { IsNew: false };

    /// <summary>
    /// Whether the section loader is shown in place of the editor while an opened list loads (#193).
    /// </summary>
    public bool ShowSettingsLoader => IsSectionSettings && RoutingEditor is not null && SectionLoading;

    /// <summary>
    /// Whether the loaded settings rule + traffic editor is shown.
    /// </summary>
    public bool ShowSettingsEditor => IsSectionSettings && RoutingEditor is not null && !SectionLoading;

    /// <summary>
    /// Whether the Delete card is shown (a real, saved list in the Settings section).
    /// </summary>
    public bool ShowDeleteCard => IsSectionSettings && RoutingEditor is { IsNew: false } && !SectionLoading;

    /// <summary>
    /// Whether the import draft rule + traffic editor is shown.
    /// </summary>
    public bool ShowImportEditor => IsSectionImport && IsImportManual;

    /// <summary>
    /// Whether the import live QR scanner is shown.
    /// </summary>
    public bool ShowImportCamera => IsSectionImport && IsImportCamera;

    public bool IsImportPicker => ImportMethod == RoutingImportMethod.Picker;

    public bool IsImportManual => ImportMethod == RoutingImportMethod.Manual;

    public bool IsImportCamera => ImportMethod == RoutingImportMethod.Camera;

    /// <summary>
    /// Whether a live camera QR scanner is available on this platform.
    /// </summary>
    public bool CameraScanAvailable => QrCameraScannerHost.IsAvailable;

    // ---- Footer Save/Cancel bar (#143): the open-list edits (rules + traffic) are held and committed atomically
    // on the footer Save, reverted on Cancel; the same footer serves the import draft. ----

    /// <summary>
    /// Whether the open-list editors hold uncommitted changes. Not gated on the sub-section, so the footer stays
    /// up when switching Settings ↔ Export with a pending edit.
    /// </summary>
    public bool IsEditDirty =>
        (RoutingEditor?.IsDirty ?? false) || (RoutingSettings?.IsDirty ?? false);

    /// <summary>
    /// Whether the footer Save/Cancel bar is shown: an import draft, or dirty open-list edits (only while this
    /// section is the one on screen).
    /// </summary>
    public bool ShowSaveBar => IsActiveSection && (IsCreatingSectionRouting ? !IsImportPicker : IsEditDirty);

    /// <summary>
    /// Whether the footer Save button is shown: the import draft shows it once in manual entry; edits always.
    /// </summary>
    public bool ShowSaveButton => !IsCreatingSectionRouting || IsImportManual;

    /// <summary>
    /// Whether the footer Save button is enabled. A list this device cannot carry is not saved at all.
    /// </summary>
    public bool CanSave => RoutingEditor is not { RouteBudgetExceeded: true }
        && (IsCreatingSectionRouting
            ? RoutingEditor is { IsNameMissing: false, HasAnyRule: true }
            : IsEditDirty);

    /// <summary>
    /// Whether the footer says the list turns into more routes than the device carries.
    /// </summary>
    public bool ShowRouteBudgetWarning => RoutingEditor is { RouteBudgetExceeded: true };

    /// <summary>
    /// The warning line above the footer buttons.
    /// </summary>
    public string RouteBudgetWarning => RoutingEditor is { RouteBudgetExceeded: true } editor
        ? Loc.Instance.Get("RoutingEditor_TooManyRoutes", editor.RouteCount, editor.RouteLimit)
        : string.Empty;

    private void RefreshEditBar()
    {
        OnPropertyChanged(nameof(IsEditDirty));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(ShowSaveButton));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowRouteBudgetWarning));
        OnPropertyChanged(nameof(RouteBudgetWarning));
    }

    // Re-raise the computed section flags after an observable driver changes.
    private void RefreshSections()
    {
        OnPropertyChanged(nameof(IsSectionImport));
        OnPropertyChanged(nameof(IsSectionSettings));
        OnPropertyChanged(nameof(IsSectionExport));
        OnPropertyChanged(nameof(ShowSettingsLoader));
        OnPropertyChanged(nameof(ShowSettingsEditor));
        OnPropertyChanged(nameof(ShowDeleteCard));
        OnPropertyChanged(nameof(ShowImportEditor));
        OnPropertyChanged(nameof(ShowImportCamera));
        OnPropertyChanged(nameof(IsImportPicker));
        OnPropertyChanged(nameof(IsImportManual));
        OnPropertyChanged(nameof(IsImportCamera));
        OnPropertyChanged(nameof(ShowHeaderActions));
        OnPropertyChanged(nameof(ShowCompactActive));
        OnPropertyChanged(nameof(CanExportOpenList));
        RefreshEditBar();
    }

    private void OnEditScopeDirty(object? sender, EventArgs e) => RefreshEditBar();

    partial void OnIsCreatingSectionRoutingChanged(bool value) => RefreshSections();

    partial void OnManageSectionChanged(RoutingSection value) => RefreshSections();

    partial void OnSectionLoadingChanged(bool value) => RefreshSections();

    partial void OnImportMethodChanged(RoutingImportMethod value) => RefreshSections();

    partial void OnHasRoutingListsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoListsHint));
    }

    // Keeps the list that routes: the section lands on it next time, and turning routing off leaves it behind
    // instead of sending the section back to the top of the catalogue.
    partial void OnSelectedRoutingListIdChanged(long? value)
    {
        SyncHomeRouting();
        if (value is not { } id || _prefs.LastRoutingList == id)
        {
            return;
        }

        _prefs.LastRoutingList = id;
        _prefs.Save();
    }

    /// <summary>
    /// Reconciles the routing-list catalogue from the snapshot.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        // No catalogue in the snapshot means the agent has not read its store yet - keep what is on screen and
        // stay "not loaded", rather than reading it as an account with no lists.
        if (snapshot.RoutingLists is { } entries)
        {
            SyncRoutingLists(entries);
            HasRoutingLists = RoutingLists.Count > 0;
            MarkCatalogueKnown();
        }

        SelectedRoutingListId = snapshot.SelectedRoutingList;
        RoutingSettings?.ApplyRouteTtl(snapshot.RouteTtlSeconds);
    }

    /// <summary>
    /// Whether the section waits for a catalogue the agent has not reported yet.
    /// </summary>
    public bool ShowCatalogueLoader => _enterDeferred;

    /// <summary>
    /// Whether the header says there are no saved lists; silent until the catalogue is known.
    /// </summary>
    public bool ShowNoListsHint => _catalogueKnown && !HasRoutingLists;

    // The first snapshot settles the catalogue: an empty one now means there are no lists, and a section held
    // on its loader can finally land somewhere.
    private void MarkCatalogueKnown()
    {
        if (_catalogueKnown)
        {
            return;
        }

        _catalogueKnown = true;
        OnPropertyChanged(nameof(ShowNoListsHint));
        if (!_enterDeferred)
        {
            return;
        }

        _enterDeferred = false;
        OnPropertyChanged(nameof(ShowCatalogueLoader));
        OnPropertyChanged(nameof(ShowHeaderActions));
        if (IsActiveSection)
        {
            EnterSection();
        }
    }

    /// <summary>
    /// Whether the open list is the one every config routes by.
    /// </summary>
    public bool IsOpenListActive => EditRoutingList is { } open && SelectedRoutingListId == open.Id;

    /// <summary>
    /// Whether every config routes by the open list. Setting it makes the open list the picked one, or leaves
    /// every config on its own settings.
    /// </summary>
    public bool UseOpenList
    {
        get => IsOpenListActive;
        set
        {
            if (value == IsOpenListActive)
            {
                return;
            }

            _ = AssignRoutingAsync(value && EditRoutingList is { } open ? open.Id : null);
        }
    }

    // Sends the assignment, then re-reads the switch from the state the agent answered with.
    private async Task AssignRoutingAsync(long? listId)
    {
        var id = listId is { } picked
            ? picked.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "none";
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAssignRouting, [id]));
        OnPropertyChanged(nameof(UseOpenList));
    }

    public void Reset()
    {
        _catalogueKnown = false;
        _enterDeferred = false;
        OnPropertyChanged(nameof(ShowCatalogueLoader));
        OnPropertyChanged(nameof(ShowHeaderActions));
        OnPropertyChanged(nameof(ShowNoListsHint));
        RoutingLists.Clear();
        HasRoutingLists = false;
        SelectedRoutingListId = null;
        RoutingEditor = null;
        RoutingSettings = null;
        EditRoutingList = null;
        _pendingEditRoutingListId = null;
        _listBeforeCreate = null;
        IsCreatingSectionRouting = false;
        ImportMethod = RoutingImportMethod.Picker;
        SectionScan = null;
        ManageSection = RoutingSection.Settings;
        ReconcileRoutingCatalogueOptions();
        SyncCatalogueRouting();
    }

    // Entering the routing section: keep an in-progress draft, land on the first list, or fall back to Import
    // when there are no lists to show (mirrors the Config section).
    public void EnterSection()
    {
        if (IsCreatingSectionRouting)
        {
            return;
        }

        if (RoutingLists.Count == 0)
        {
            // The agent has not reported its lists yet: hold the section on a loader. Opening the import draft
            // here would stick, since a later catalogue never pulls the section back out of it (#134).
            if (!_catalogueKnown)
            {
                _enterDeferred = true;
                OnPropertyChanged(nameof(ShowCatalogueLoader));
                OnPropertyChanged(nameof(ShowHeaderActions));
                return;
            }

            BeginSectionRouting();
            return;
        }

        SelectFirstIfNone();
    }

    // Landing on the Routing section with nothing open: open the list that routes so it never opens empty, and
    // never on a list the user did not pick. A new-list draft in progress is left alone.
    public void SelectFirstIfNone()
    {
        if (EditRoutingList is null && RoutingEditor is not { IsNew: true } && RoutingLists.Count > 0)
        {
            EditRoutingList = PreferredDefaultList();
        }
    }

    // The list every config routes by, else the one that did last, else the first in the catalogue.
    private RoutingListSummaryViewModel PreferredDefaultList()
    {
        return CatalogueRow(SelectedRoutingListId) ?? CatalogueRow(_prefs.LastRoutingList) ?? RoutingLists[0];
    }

    // The catalogue row carrying this id, while it is still there.
    private RoutingListSummaryViewModel? CatalogueRow(long? id)
    {
        return id is > 0 ? RoutingLists.FirstOrDefault(r => r.Id == id) : null;
    }

    // Reflect the globally selected routing list into this section: a real list opens there (its rule /
    // per-routing settings editors build via OnEditRoutingListChanged), while none clears the section.
    public void OpenSelected(long? listId)
    {
        if (listId is { } id && RoutingLists.FirstOrDefault(r => r.Id == id) is { } row)
        {
            EditRoutingList = row;
            return;
        }

        EditRoutingList = null;
        RoutingEditor = null;
        RoutingSettings = null;
        SyncCatalogueRouting();
    }

    // Top menu: Settings / Import / Export. Import begins a fresh create draft; Settings / Export land on the
    // open list (or the first when none is open) and pick the sub-section.
    [RelayCommand]
    private void SelectRoutingSection(string target)
    {
        if (target == "import")
        {
            if (!IsCreatingSectionRouting)
            {
                BeginSectionRouting();
            }

            return;
        }

        LeaveImport();
        SelectFirstIfNone();
        ManageSection = target == "export" ? RoutingSection.Export : RoutingSection.Settings;
        if (ManageSection == RoutingSection.Export)
        {
            RoutingEditor?.EnsureTransfer();
        }
    }

    /// <summary>
    /// Возвращает с экрана экспорта и из черновика импорта туда, откуда их открыли. Отдаёт, сделан ли шаг.
    /// </summary>
    public bool TryNavigateBack()
    {
        if (IsSectionExport)
        {
            SelectRoutingSection("settings");
            return true;
        }

        if (!IsCreatingSectionRouting)
        {
            return false;
        }

        AbandonCreate();
        return true;
    }

    // Discard an in-progress draft before switching to Settings / Export.
    private void LeaveImport()
    {
        if (IsCreatingSectionRouting)
        {
            AbandonCreate();
        }
    }

    // The Routing section's edit combo changed: build the rule editor and per-routing settings for the
    // selected list. A null pick is ignored (combo-rebuild artifact); the import path is a command.
    partial void OnEditRoutingListChanged(RoutingListSummaryViewModel? value)
    {
        // Switching (or clearing) the selected list disarms any pending delete confirmation (#143).
        RoutingDeletePending = false;
        RoutingDeleteStatus = string.Empty;

        if (value is null)
        {
            return;
        }

        BuildSectionRoutingEditor(value.Id, value.Name);
        // Selecting the just-saved list (or re-selecting the open one) short-circuits BuildSectionRoutingEditor,
        // so RoutingEditor does not change and OnRoutingEditorChanged will not fire - mirror the combo here.
        SyncCatalogueRouting();
    }

    // The Routing section combo pick: «— не выбрано —» closes the editor; a real list opens it for editing. A
    // new-list draft is opened by the «Импорт» tab (#111), not from this combo.
    partial void OnSelectedCatalogueRoutingChanged(RoutingListChoice? value)
    {
        if (_suppressCatalogueRouting || value is null)
        {
            return;
        }

        if (value.IsNone)
        {
            EditRoutingList = null;
            RoutingEditor = null;
            RoutingSettings = null;
            return;
        }

        var row = RoutingLists.FirstOrDefault(r => r.Id == value.Id);
        if (row is not null)
        {
            EditRoutingList = row;
        }
    }

    // The section rule-editor instance changed (new draft created, real list opened, or closed): re-mirror the
    // combo, subscribe the dirty signal, and re-hook the edit listeners that clear a stale "cannot delete" block.
    partial void OnRoutingEditorChanged(RoutingListEditorViewModel? oldValue, RoutingListEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditPropertyChanged;
            oldValue.Rules.CollectionChanged -= OnEditCollectionChanged;
            oldValue.DirtyChanged -= OnEditScopeDirty;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditPropertyChanged;
            newValue.Rules.CollectionChanged += OnEditCollectionChanged;
            newValue.DirtyChanged += OnEditScopeDirty;
        }

        SyncCatalogueRouting();
        SyncTrafficFlags();
        RefreshSections();
    }

    // The per-routing settings editor changed: re-hook the edit listener and subscribe the dirty signal.
    partial void OnRoutingSettingsChanged(RoutingSettingsViewModel? oldValue, RoutingSettingsViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditPropertyChanged;
            oldValue.DirtyChanged -= OnEditScopeDirty;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditPropertyChanged;
            newValue.DirtyChanged += OnEditScopeDirty;
        }

        SyncTrafficFlags();
        RefreshEditBar();
    }

    // Any edit to the open list (a field or the rule set) clears a lingering delete line and refreshes the Save bar.
    private void OnEditPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RoutingDeleteStatus = string.Empty;
        SyncTrafficFlags();
        RefreshEditBar();
    }

    // Feeds the traffic card's flags into the rule editor: the Proxy bucket warns when global proxy makes it
    // unused, and both flags travel with the exported payload.
    private void SyncTrafficFlags()
    {
        if (RoutingEditor is { } editor)
        {
            editor.GlobalProxyActive = RoutingSettings?.UseGlobalProxy ?? false;
            editor.AllUdpActive = RoutingSettings?.AllUdp ?? false;
        }
    }

    private void OnEditCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RoutingDeleteStatus = string.Empty;
        RefreshEditBar();
    }

    // Mirror the Routing section's state into its combo without echoing the pick back: a selected real list
    // shows its row, otherwise «— не выбрано —» (including while a new-list draft is being created).
    private void SyncCatalogueRouting()
    {
        _suppressCatalogueRouting = true;
        SelectedCatalogueRouting = EditRoutingList is null
            ? RoutingListChoice.None
            : RoutingCatalogueOptions.FirstOrDefault(o => o.IsReal && o.Id == EditRoutingList.Id) ?? RoutingListChoice.None;
        _suppressCatalogueRouting = false;
    }

    // Reconcile RoutingCatalogueOptions in place from RoutingLists: reconcile the real (id) choices - replacing a
    // renamed row so its label updates. A following SyncCatalogueRouting re-selects by id, so a replace never
    // strands the selection.
    private void ReconcileRoutingCatalogueOptions()
    {
        ReconcileOptions(RoutingCatalogueOptions, head: 0);
        ReconcileOptions(HomeRoutingOptions, head: 1);
        SyncHomeRouting();
    }

    // The same reconcile over one option list; head is what stands ahead of the catalogue and is left alone.
    private void ReconcileOptions(ObservableCollection<RoutingListChoice> options, int head)
    {
        var present = RoutingLists.Select(r => r.Id).ToHashSet();
        for (var i = options.Count - 1; i >= head; i--)
        {
            if (options[i].Id is not long id || !present.Contains(id))
            {
                options.RemoveAt(i);
            }
        }

        for (var i = 0; i < RoutingLists.Count; i++)
        {
            var row = RoutingLists[i];
            var slot = head + i;
            var existing = options.Skip(head).FirstOrDefault(o => o.Id == row.Id);
            if (existing is null)
            {
                options.Insert(Math.Min(slot, options.Count), new RoutingListChoice(row.Id, row.Name));
                continue;
            }

            if (!string.Equals(existing.Name, row.Name, StringComparison.Ordinal))
            {
                options[options.IndexOf(existing)] = new RoutingListChoice(row.Id, row.Name);
                existing = options.Skip(head).First(o => o.Id == row.Id);
            }

            var index = options.IndexOf(existing);
            if (index != slot)
            {
                options.Move(index, slot);
            }
        }
    }

    // Mirrors the list that routes into the server screen's picker without echoing the pick back.
    private void SyncHomeRouting()
    {
        _suppressHomeRouting = true;
        HomeRoutingChoice = SelectedRoutingListId is { } id
            ? HomeRoutingOptions.FirstOrDefault(o => o.IsReal && o.Id == id) ?? RoutingListChoice.None
            : RoutingListChoice.None;
        _suppressHomeRouting = false;
    }

    // Builds the Routing section's rule editor + per-routing settings for a real (saved) list. Independent of
    // the selection - the section catalogue is standalone.
    private void BuildSectionRoutingEditor(long id, string name)
    {
        if (RoutingEditor is not null && RoutingEditor.Id == id && !RoutingEditor.IsNew)
        {
            return;
        }

        var editor = new RoutingListEditorViewModel(_connection, id, name, OnSectionRoutingEditorSaved);
        RoutingEditor = editor;

        var settings = new RoutingSettingsViewModel(_connection, id);
        RoutingSettings = settings;

        _ = LoadSectionAsync(editor, settings);
    }

    // Holds the section loader until the opened list's rules and traffic settings both finish, then reveals the
    // editor fully populated so nothing reflows (#193). A superseding open leaves the stale load's clear to the
    // newer one (the editor instance no longer matches).
    private async Task LoadSectionAsync(RoutingListEditorViewModel editor, RoutingSettingsViewModel settings)
    {
        SectionLoading = true;
        try
        {
            await Task.WhenAll(editor.LoadAsync(), settings.LoadAsync());
        }
        finally
        {
            if (ReferenceEquals(RoutingEditor, editor))
            {
                SectionLoading = false;
            }
        }
    }

    // When the Routing section's new (id=0) list is first saved it gets a real id: retarget its per-routing
    // settings and pin the selection to the freshly-created list. The list's summary row is not in
    // RoutingLists yet (it arrives on the next snapshot), so remember the id and let SyncRoutingLists select
    // it once present; if it is already there, select it now.
    private void OnSectionRoutingEditorSaved(long id)
    {
        if (RoutingSettings is { } draft)
        {
            draft.Retarget(id);
        }
        else
        {
            var settings = new RoutingSettingsViewModel(_connection, id);
            RoutingSettings = settings;
            _ = settings.LoadAsync();
        }

        var created = RoutingLists.FirstOrDefault(r => r.Id == id);
        if (created is not null)
        {
            _pendingEditRoutingListId = null;
            EditRoutingList = created;
        }
        else
        {
            _pendingEditRoutingListId = id;
        }
    }

    private void SyncRoutingLists(IReadOnlyList<RoutingListEntry> entries)
    {
        // Reconcile in place (match by id) so the selected-list highlight is not dropped and re-set on every
        // snapshot.
        var present = entries.Select(e => e.Id).ToHashSet();
        for (var i = RoutingLists.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(RoutingLists[i].Id))
            {
                RoutingLists.RemoveAt(i);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = RoutingLists.FirstOrDefault(r => r.Id == entry.Id);
            if (existing is null)
            {
                existing = new RoutingListSummaryViewModel { Id = entry.Id };
                RoutingLists.Insert(Math.Min(i, RoutingLists.Count), existing);
            }
            else
            {
                var from = RoutingLists.IndexOf(existing);
                if (from != i)
                {
                    RoutingLists.Move(from, i);
                }
            }

            existing.Name = entry.Name;
            existing.RuleCount = entry.RuleCount;
            existing.RouteCount = entry.RouteCount;
            existing.DomainCount = entry.DomainCount;
        }

        ReconcileRoutingCatalogueOptions();

        // Reconcile the selected list: if removed elsewhere drop the editor; if its instance was replaced by a
        // fresh row of the same id, re-point at the surviving instance so the combo stays selected. A pending
        // new-list draft (RoutingEditor.IsNew) is left alone.
        if (EditRoutingList is not null && !RoutingLists.Contains(EditRoutingList))
        {
            var same = RoutingLists.FirstOrDefault(r => r.Id == EditRoutingList.Id);
            if (same is not null)
            {
                EditRoutingList = same;
            }
            else if (RoutingEditor is not { IsNew: true })
            {
                EditRoutingList = null;
                RoutingEditor = null;
                RoutingSettings = null;
            }
        }

        // A list just created here: once its summary row arrives, select it so the combo shows it and «Удалить»
        // becomes available. The editor already cleared IsNew on its first save, so selecting it short-circuits
        // BuildSectionRoutingEditor (no rebuild, no re-fetch, no lost edits).
        if (_pendingEditRoutingListId is long pendingId)
        {
            var row = RoutingLists.FirstOrDefault(r => r.Id == pendingId);
            if (row is not null)
            {
                _pendingEditRoutingListId = null;
                EditRoutingList = row;
            }
        }

        SyncCatalogueRouting();
    }

    // ---- Import create-form: "+ Импорт" opens a new-list draft with a method picker (blank / file / paste / QR). ----

    // "Импорт": show a fresh create-editor with the SAME form as an existing list - rules AND the per-routing
    // traffic settings - just with empty fields, so everything can be set up before the first save (#5). The
    // draft settings target id 0 until the list is created, then get retargeted at the real id.
    private void BeginSectionRouting()
    {
        // Remember the open list so Cancel restores it (or «— не выбрано —»).
        _listBeforeCreate = EditRoutingList;

        // A new draft has no server data to load: show its empty form at once, never the section loader (#193).
        SectionLoading = false;

        // Clear the selected list first: setting RoutingEditor below fires OnRoutingEditorChanged ->
        // SyncCatalogueRouting, which mirrors EditRoutingList into the combo, so it must already be null for
        // the combo to read «— не выбрано —» while the new draft is being created.
        RoutingSettings = null;
        EditRoutingList = null;
        var editor = new RoutingListEditorViewModel(_connection, OnSectionRoutingEditorSaved);
        RoutingEditor = editor;
        _ = editor.LoadAsync();

        // Draft traffic settings (id 0, no load - a new list has none server-side). Committed once the list is
        // created and retargeted at the real id (#5).
        RoutingSettings = new RoutingSettingsViewModel(_connection, 0);

        ImportMethod = RoutingImportMethod.Picker;
        SectionScan = null;
        IsCreatingSectionRouting = true;
    }

    // Открывает черновик, если он ещё не начат: способ выбирается в шторке «Добавить», а не вкладкой.
    private void EnsureSectionRouting()
    {
        if (!IsCreatingSectionRouting)
        {
            BeginSectionRouting();
        }
    }

    /// <summary>
    /// Открывает черновик под разобранный файл, буфер обмена или снимок.
    /// </summary>
    public void BeginImportDraft()
    {
        EnsureSectionRouting();
        ImportMethod = RoutingImportMethod.Manual;
    }

    // Ссылка «Источники geo» над записями: гео-базы правятся оттуда, куда их подставляют.
    [RelayCommand]
    private void OpenGeoSources()
    {
        _host.ShowGeoSources();
    }

    // Способ «Создать вручную».
    [RelayCommand]
    private void BeginManualImport()
    {
        BeginImportDraft();
    }

    // Способ «Сканировать QR-код».
    [RelayCommand]
    private void BeginCameraImport()
    {
        if (!QrCameraScannerHost.IsAvailable)
        {
            return;
        }

        EnsureSectionRouting();
        SectionScan = new ScanViewModel(TryAcceptScannedRouting);
        ImportMethod = RoutingImportMethod.Camera;
    }

    // Applies an imported blob (from file / clipboard / QR) into the draft editor and reveals it for review.
    public void ApplyImportText(string text)
    {
        if (RoutingEditor is not { } editor)
        {
            return;
        }

        if (editor.ApplyImport(text, out var options))
        {
            ResolveImportedName(editor);
            if (options is not null && RoutingSettings is { } settings)
            {
                settings.AllUdp = options.AllUdp;
                settings.UseGlobalProxy = options.UseGlobalProxy;
            }
        }

        SectionScan = null;
        ImportMethod = RoutingImportMethod.Manual;
    }

    // The imported name may be taken by another list; the save would be refused, so land on a free one and say so.
    private void ResolveImportedName(RoutingListEditorViewModel editor)
    {
        var taken = RoutingLists.Where(r => r.Id != editor.Id).Select(r => r.Name);
        var free = UniqueName.Resolve(editor.Name, taken);
        if (string.Equals(free, editor.Name, StringComparison.Ordinal))
        {
            return;
        }

        var clashed = editor.Name;
        editor.Name = free;
        editor.StatusMessage = Loc.Instance.Get("RoutingEditor_ImportedNameTaken", clashed, free);
    }

    /// <summary>
    /// Импортирует брошенный драгом список маршрутизации: уникализирует имя, сохраняет и выбирает.
    /// </summary>
    public async Task<bool> ImportDroppedListAsync(string text, ISet<string> reserved)
    {
        if (!PortableTransfer.TryDecodeRouting(text, out var importedName, out _))
        {
            return false;
        }

        var editor = new RoutingListEditorViewModel(_connection);
        editor.ApplyImport(text, out var options);
        var baseName = string.IsNullOrWhiteSpace(importedName)
            ? Loc.Instance.Get("MainVm_NewListDefaultName")
            : importedName;
        editor.Name = UniqueName.Resolve(baseName, reserved);

        if (!await editor.SaveAsync())
        {
            return false;
        }

        if (options is not null)
        {
            var settings = new RoutingSettingsViewModel(_connection, editor.Id)
            {
                AllUdp = options.AllUdp,
                UseGlobalProxy = options.UseGlobalProxy,
            };
            await settings.CommitAsync();
        }

        reserved.Add(editor.Name);

        if (!IsCreatingSectionRouting && !IsEditDirty)
        {
            var row = RoutingLists.FirstOrDefault(r => r.Id == editor.Id);
            if (row is not null)
            {
                _pendingEditRoutingListId = null;
                EditRoutingList = row;
            }
            else
            {
                _pendingEditRoutingListId = editor.Id;
            }
        }

        return true;
    }

    // The scanner reports a decoded QR's raw text; accept it only when it decodes to a routing list.
    private bool TryAcceptScannedRouting(string text)
    {
        if (!PortableTransfer.TryDecodeRouting(text, out _, out _))
        {
            return false;
        }

        ApplyImportText(text);
        return true;
    }

    // Footer Save/Cancel: the same bar serves the import draft and the open-list edits.
    [RelayCommand]
    private async Task SaveSection()
    {
        _host.ArmReconnectPrompt();
        if (IsCreatingSectionRouting)
        {
            await SaveNewList();
        }
        else
        {
            await SaveRoutingEdit();
        }
    }

    // Footer Cancel: an import draft returns to the method picker in place (discards the drafted rules, no
    // navigation); an open-list edit reverts to its baseline. Leaving the import section fully is the top tabs.
    [RelayCommand]
    private void CancelSection()
    {
        if (IsCreatingSectionRouting)
        {
            AbandonCreate();
        }
        else
        {
            CancelRoutingEdit();
        }
    }

    // Footer Save (open list): commit the dirty rules and traffic settings atomically. A rejected step surfaces
    // its own reason and leaves the rest pending.
    private async Task SaveRoutingEdit()
    {
        if (RoutingEditor is { IsDirty: true } editor)
        {
            if (!await editor.CommitAsync())
            {
                RefreshEditBar();
                return;
            }

            editor.CaptureBaseline();
        }

        if (RoutingSettings is { IsDirty: true } settings)
        {
            if (!await settings.CommitAsync())
            {
                RefreshEditBar();
                return;
            }

            settings.CaptureBaseline();
        }

        RefreshEditBar();
    }

    // Footer Cancel (open list): revert the rules and traffic settings to their loaded baseline.
    private void CancelRoutingEdit()
    {
        RoutingEditor?.Revert();
        RoutingSettings?.Revert();
        RefreshEditBar();
    }

    // Footer Save (import draft): create the list, then commit its traffic settings against the new id.
    private async Task SaveNewList()
    {
        if (RoutingEditor is null)
        {
            return;
        }

        // CommitAsync validates name + at least one rule, then on a new list adopts the real id, clears IsNew,
        // and calls OnSectionRoutingEditorSaved (retargets the draft settings, selects the created list).
        if (!await RoutingEditor.CommitAsync())
        {
            RefreshEditBar();
            return;
        }

        RoutingEditor.CaptureBaseline();

        if (RoutingSettings is { IsDirty: true } settings)
        {
            if (await settings.CommitAsync())
            {
                settings.CaptureBaseline();
            }
        }

        IsCreatingSectionRouting = false;
        ImportMethod = RoutingImportMethod.Picker;
        SectionScan = null;
        _listBeforeCreate = null;
        ManageSection = RoutingSection.Settings;
        RefreshSections();
    }

    // Footer Cancel (import draft): discard the draft and restore the list open before "+ Импорт".
    private void CancelNewList()
    {
        IsCreatingSectionRouting = false;
        ImportMethod = RoutingImportMethod.Picker;
        SectionScan = null;
        RoutingEditor = null;
        RoutingSettings = null;
        ManageSection = RoutingSection.Settings;
        EditRoutingList = _listBeforeCreate;
        _listBeforeCreate = null;
        if (EditRoutingList is null)
        {
            SyncCatalogueRouting();
        }
    }

    /// <summary>
    /// Drops the geo entries fetched into the open editor when the section is left.
    /// </summary>
    public void LeaveSection()
    {
        RoutingEditor?.ClearRuleDetails();
    }

    // Discard the import draft when the routing section is left for another one.
    public void AbandonCreate()
    {
        if (IsCreatingSectionRouting)
        {
            CancelNewList();
        }
    }

    // The routing-list Delete trigger (#143): a new unsaved draft is not offered here (hidden while IsNew).
    // Otherwise arm the inline confirm/cancel pair (#4).
    [RelayCommand]
    private void RequestDeleteSectionRoutingList()
    {
        if (RoutingEditor is null || RoutingEditor.IsNew)
        {
            return;
        }

        RoutingDeleteStatus = string.Empty;
        RoutingDeletePending = true;
    }

    // Inline Cancel: disarm the routing-list delete confirm.
    [RelayCommand]
    private void CancelDeleteRouting()
    {
        RoutingDeletePending = false;
        RoutingDeleteStatus = string.Empty;
    }

    // Inline Confirm: delete the shared list. The usage guard is re-checked, then on success the next remaining
    // list is selected (or the editor cleared when it was the last one) so the section is never left empty.
    [RelayCommand]
    private async Task ConfirmDeleteSectionRoutingList()
    {
        RoutingDeletePending = false;
        if (RoutingEditor is null)
        {
            return;
        }

        var deletedId = RoutingEditor.Id;

        if (!await RoutingEditor.DeleteAsync())
        {
            RoutingDeleteStatus = RoutingEditor.StatusMessage;
            return;
        }

        var next = RoutingLists.FirstOrDefault(r => r.Id != deletedId);
        if (next is not null)
        {
            EditRoutingList = next;
        }
        else
        {
            EditRoutingList = null;
            RoutingEditor = null;
            RoutingSettings = null;
        }
    }
}

/// <summary>
/// Routing screen manage sub-section.
/// </summary>
internal enum RoutingSection
{
    Settings,
    Export,
}

/// <summary>
/// Routing import create-form method.
/// </summary>
internal enum RoutingImportMethod
{
    Picker,
    Manual,
    Camera,
}
