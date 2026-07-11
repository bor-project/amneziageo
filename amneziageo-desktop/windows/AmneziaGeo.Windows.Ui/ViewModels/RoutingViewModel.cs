using System.Collections.ObjectModel;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Routing screen: the routing-list catalogue, the rule/per-routing editors, and list CRUD. The shared
/// catalogue edit-lock (<see cref="MainWindowViewModel.IsEditing"/>), the profile list, and the edit-scope
/// re-point live on the shell, reached through <c>_host</c>.
/// </summary>
internal sealed partial class RoutingViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly AgentConnection _connection;

    private long? _pendingEditRoutingListId;
    private bool _suppressCatalogueRouting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoutingEditor))]
    private RoutingListEditorViewModel? _routingEditor;

    [ObservableProperty]
    private RoutingSettingsViewModel? _routingSettings;

    [ObservableProperty]
    private RoutingListSummaryViewModel? _editRoutingList;

    [ObservableProperty]
    private RoutingListChoice? _selectedCatalogueRouting = RoutingListChoice.None;

    [ObservableProperty]
    private bool _routingDeletePending;

    [ObservableProperty]
    private string _routingDeleteStatus = string.Empty;

    [ObservableProperty]
    private bool _hasRoutingLists;

    /// <summary>
    /// Routing-list catalogue.
    /// </summary>
    public ObservableCollection<RoutingListSummaryViewModel> RoutingLists { get; } = [];

    public ObservableCollection<RoutingListChoice> RoutingCatalogueOptions { get; } = [RoutingListChoice.None];

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingViewModel(MainWindowViewModel host, AgentConnection connection)
    {
        _host = host;
        _connection = connection;
    }

    /// <summary>
    /// Whether the rule editor is shown (a list is selected or a new draft is open).
    /// </summary>
    public bool HasRoutingEditor => RoutingEditor is not null;

    /// <summary>
    /// The shared catalogue edit-lock, surfaced for this screen's controls.
    /// </summary>
    public bool IsEditing => _host.IsEditing;

    /// <summary>
    /// Reconciles the routing-list catalogue from the snapshot.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        SyncRoutingLists(snapshot.RoutingLists ?? []);
        HasRoutingLists = RoutingLists.Count > 0;
    }

    public void Reset()
    {
        RoutingLists.Clear();
        HasRoutingLists = false;
        RoutingEditor = null;
        RoutingSettings = null;
        EditRoutingList = null;
        _pendingEditRoutingListId = null;
        ReconcileRoutingCatalogueOptions();
        SyncCatalogueRouting();
    }

    // Re-raise IsEditing when the shared edit-lock flips (the shell owns EditController).
    public void NotifyIsEditingChanged()
    {
        OnPropertyChanged(nameof(IsEditing));
    }

    // Discards an unsaved new-list draft (the shell's Cancel path, #143).
    public void CancelNewDraft()
    {
        RoutingEditor = null;
        RoutingSettings = null;
        EditRoutingList = null;
    }

    // Reflect a profile's assigned routing list into this section: a real list opens there (its rule /
    // per-routing settings editors build via OnEditRoutingListChanged), while no list clears the section.
    public void OpenForProfile(ProfileItemViewModel? profile)
    {
        var choice = profile?.SelectedRoutingList;
        if (choice is { IsReal: true } && RoutingLists.FirstOrDefault(r => r.Id == choice.Id) is { } row)
        {
            EditRoutingList = row;
            return;
        }

        EditRoutingList = null;
        RoutingEditor = null;
        RoutingSettings = null;
        SyncCatalogueRouting();
    }

    // The Routing section's edit combo changed: build the rule editor and per-routing settings for the
    // selected list. A null pick is ignored (combo-rebuild artifact); the create-new path is a command.
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
    // new-list draft is opened by the «+ Новый список» button (#111), not from this combo.
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

    // The section rule-editor instance changed (new draft created, real list opened, or closed): re-mirror
    // the combo and re-point the shared edit controller (#143).
    partial void OnRoutingEditorChanged(RoutingListEditorViewModel? oldValue, RoutingListEditorViewModel? newValue)
    {
        SyncCatalogueRouting();
        _host.RefreshEditScopes();
    }

    // The per-routing settings editor was (re)built or cleared: re-point the shared edit controller (#143).
    partial void OnRoutingSettingsChanged(RoutingSettingsViewModel? oldValue, RoutingSettingsViewModel? newValue)
    {
        _host.RefreshEditScopes();
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

    // Reconcile RoutingCatalogueOptions in place from RoutingLists: keep «— не выбрано —» at [0] and reconcile
    // the real (id) choices after it - replacing a renamed row so its label updates. A following
    // SyncCatalogueRouting re-selects by id, so a replace never strands the selection.
    private void ReconcileRoutingCatalogueOptions()
    {
        const int head = 1; // None occupies [0].
        var present = RoutingLists.Select(r => r.Id).ToHashSet();
        for (var i = RoutingCatalogueOptions.Count - 1; i >= head; i--)
        {
            if (RoutingCatalogueOptions[i].Id is not long id || !present.Contains(id))
            {
                RoutingCatalogueOptions.RemoveAt(i);
            }
        }

        for (var i = 0; i < RoutingLists.Count; i++)
        {
            var row = RoutingLists[i];
            var slot = head + i;
            var existing = RoutingCatalogueOptions.Skip(head).FirstOrDefault(o => o.Id == row.Id);
            if (existing is null)
            {
                RoutingCatalogueOptions.Insert(Math.Min(slot, RoutingCatalogueOptions.Count), new RoutingListChoice(row.Id, row.Name));
                continue;
            }

            if (!string.Equals(existing.Name, row.Name, StringComparison.Ordinal))
            {
                RoutingCatalogueOptions[RoutingCatalogueOptions.IndexOf(existing)] = new RoutingListChoice(row.Id, row.Name);
                existing = RoutingCatalogueOptions.Skip(head).First(o => o.Id == row.Id);
            }

            var index = RoutingCatalogueOptions.IndexOf(existing);
            if (index != slot)
            {
                RoutingCatalogueOptions.Move(index, slot);
            }
        }
    }

    // Builds the Routing section's rule editor + per-routing settings for a real (saved) list. Independent of
    // any open profile - the section catalogue is standalone.
    private void BuildSectionRoutingEditor(long id, string name)
    {
        if (RoutingEditor is not null && RoutingEditor.Id == id && !RoutingEditor.IsNew)
        {
            return;
        }

        var editor = new RoutingListEditorViewModel(_connection, id, name, OnSectionRoutingEditorSaved);
        RoutingEditor = editor;
        _ = editor.LoadAsync();

        var settings = new RoutingSettingsViewModel(_connection, id);
        RoutingSettings = settings;
        _ = settings.LoadAsync();
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

    // "+ Новый список": show a fresh create-editor with the SAME form as an existing list - rules AND the
    // per-routing traffic settings - just with empty fields, so everything can be set up before the first
    // save (#5). The draft settings target id 0 until the list is created, then get retargeted at the real id.
    [RelayCommand]
    private void CreateRoutingList()
    {
        // Clear the selected list first: setting RoutingEditor below fires OnRoutingEditorChanged ->
        // SyncCatalogueRouting, which mirrors EditRoutingList into the combo, so it must already be null for
        // the combo to read «— не выбрано —» while the new draft is being created.
        RoutingSettings = null;
        EditRoutingList = null;
        var editor = new RoutingListEditorViewModel(_connection, OnSectionRoutingEditorSaved);
        // Pre-fill a unique default name (#117) so the required-name field is never empty on open. Set before
        // LoadAsync, while the editor still suppresses auto-save, so seeding the name does not schedule a save.
        editor.Name = UniqueRoutingListName();
        RoutingEditor = editor;
        _ = editor.LoadAsync();

        // Draft traffic settings (id 0, no load - a new list has none server-side). They commit with the list
        // on the header Save, once retargeted at the created id (#5).
        RoutingSettings = new RoutingSettingsViewModel(_connection, 0);
    }

    // The routing-list Delete trigger (#143): a list any profile references cannot be deleted - surface them in
    // an error line. A new unsaved draft is not offered here (hidden while IsNew). Otherwise arm the inline
    // confirm/cancel pair (#4).
    [RelayCommand]
    private void RequestDeleteSectionRoutingList()
    {
        if (RoutingEditor is null || RoutingEditor.IsNew)
        {
            return;
        }

        RoutingDeleteStatus = string.Empty;
        var users = ProfilesUsingRouting(RoutingEditor.Id);
        if (users.Count > 0)
        {
            RoutingDeleteStatus = FormatInUse("Main_RoutingInUse", users);
            return;
        }

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
        var users = ProfilesUsingRouting(deletedId);
        if (users.Count > 0)
        {
            RoutingDeleteStatus = FormatInUse("Main_RoutingInUse", users);
            return;
        }

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

    // Profiles whose assigned routing list is this id (regardless of the use-routing toggle - the row still
    // references the list). Reliable because delete is gated on !IsEditing, so no profile is mid-edit.
    private List<string> ProfilesUsingRouting(long id) =>
        _host.Profile.Profiles.Where(b => b.SelectedRoutingList.Id == id)
            .Select(b => b.Name)
            .ToList();

    // "Cannot delete: …used by profiles:" followed by one bulleted profile per line (error-coloured in the UI).
    private static string FormatInUse(string headerKey, IReadOnlyList<string> profiles) =>
        Loc.Instance.Get(headerKey) + "\n" + string.Join("\n", profiles.Select(p => "• " + p));

    private string UniqueRoutingListName()
    {
        // Include the name still held by the open editor: a just-saved new list lags RoutingLists by one
        // snapshot, so without this a rapid second «+ Новый список» would propose the same default again.
        var taken = RoutingLists.Select(r => r.Name);
        if (RoutingEditor is { } editor)
        {
            taken = taken.Append(editor.Name);
        }

        return UniqueDefaultName(Loc.Instance.Get("MainVm_NewListDefaultName"), taken);
    }

    // A unique default name, pre-naming a new item so the required-name field is never empty on open:
    // "<base>", then "<base> 2", "<base> 3"…
    private static string UniqueDefaultName(string baseName, IEnumerable<string> taken)
    {
        var existing = taken.ToHashSet(StringComparer.Ordinal);
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
}
