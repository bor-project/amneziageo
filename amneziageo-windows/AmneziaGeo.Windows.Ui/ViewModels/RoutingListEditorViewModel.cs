using System;
using System.Collections.ObjectModel;
using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Editor for a shared routing list: name + rules (geo categories or manual domains / cidrs). Edits are
/// persisted automatically - a short debounce after the last change saves the list through the agent, so
/// there is no "Сохранить" button. The share QR is no longer inline (it lived in the edit surface, and
/// swapping the Image stole input focus); it is shown on demand in a separate dialog (BuildTransferPayload
/// feeds QrDialog).
/// </summary>
internal sealed partial class RoutingListEditorViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly Action<long>? _onSaved;
    private readonly DispatcherTimer _autoSaveTimer;
    private long _id;

    // Auto-save is suppressed while the editor is constructed and its initial rules are loaded, so seeding
    // Name/Rules does not immediately re-save unchanged data (and churn the snapshot). Cleared when LoadAsync
    // finishes; user edits after that schedule a debounced save.
    private bool _suppressAutoSave = true;

    // Consecutive failed auto-saves; bounds retrying a transient agent rejection so a single NAK does not
    // strand the edit, without spinning forever on a persistent failure.
    private int _saveFailures;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _ruleInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor used when creating a fresh routing list.
    /// </summary>
    public RoutingListEditorViewModel(AgentConnection connection, Action<long>? onSaved = null)
        : this(connection, 0, string.Empty, onSaved)
    {
        IsNew = true;
    }

    /// <summary>
    /// ctor used when editing an existing routing list (rules loaded via LoadAsync).
    /// </summary>
    public RoutingListEditorViewModel(AgentConnection connection, long id, string name, Action<long>? onSaved = null)
    {
        _connection = connection;
        _onSaved = onSaved;
        _id = id;
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _autoSaveTimer.Tick += OnAutoSaveTick;
        Rules.CollectionChanged += (_, _) => ScheduleAutoSave();
        Name = name;
    }

    /// <summary>
    /// True when this editor is creating a new list rather than editing an existing one.
    /// </summary>
    public bool IsNew { get; }

    /// <summary>
    /// The persisted list id (0 until a new list is first saved).
    /// </summary>
    public long Id => _id;

    /// <summary>
    /// The rules of this list as rule tokens (geosite:openai etc).
    /// </summary>
    public ObservableCollection<string> Rules { get; } = [];

    /// <summary>
    /// Geo category suggestions for the rule input, fetched from the agent.
    /// </summary>
    public ObservableCollection<string> GeoSuggestions { get; } = [];

    /// <summary>
    /// Fetches geo category suggestions and (for existing lists) the current rules.
    /// </summary>
    public async Task LoadAsync()
    {
        var suggestions = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListGeo, []));
        if (suggestions.Ok)
        {
            foreach (var token in suggestions.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                GeoSuggestions.Add(token);
            }
        }

        if (_id != 0)
        {
            var detail = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetRoutingList, [_id.ToString(CultureInfo.InvariantCulture)]));
            if (detail.Ok)
            {
                foreach (var token in detail.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Rules.Add(token);
                }
            }
        }

        // Seeding done; edits from here on auto-save.
        _suppressAutoSave = false;
    }

    /// <summary>
    /// Saves the list (insert or update) through the agent; returns whether it succeeded.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        var trimmed = Name.Trim();
        if (trimmed.Length == 0)
        {
            StatusMessage = "Введите имя правила";
            return false;
        }

        IsBusy = true;
        try
        {
            var args = new List<string> { _id.ToString(CultureInfo.InvariantCulture), trimmed };
            args.AddRange(Rules);
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSaveRoutingList, args));
            if (ack.Ok && long.TryParse(ack.Message, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resultId))
            {
                _id = resultId;
            }

            StatusMessage = ack.Ok ? "Сохранено" : ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Deletes the list through the agent; returns whether it succeeded.
    /// </summary>
    public async Task<bool> DeleteAsync()
    {
        CancelPendingSave();
        if (_id == 0)
        {
            return true;
        }

        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRemoveRoutingList, [_id.ToString(CultureInfo.InvariantCulture)]));
            StatusMessage = ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Cancels a pending debounced auto-save without persisting it (used when the list is being deleted).
    /// </summary>
    public void CancelPendingSave() => _autoSaveTimer.Stop();

    /// <summary>
    /// Called when the editor is detached (list switch / close / profile change / disconnect). A persisted
    /// list (Id != 0) with a queued edit is flushed so navigating away does not lose it; an un-persisted draft
    /// (Id == 0) is simply abandoned, so a half-made "+ Новый список" leaves no orphan list behind.
    /// </summary>
    public void DetachAutoSave()
    {
        var hadPending = _autoSaveTimer.IsEnabled;
        _autoSaveTimer.Stop();
        if (hadPending && _id != 0 && Name.Trim().Length != 0)
        {
            // Persist the last edit (fire-and-forget). The list is already bound, so no _onSaved is needed.
            _ = SaveAsync();
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        var text = RuleInput.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var rule = Normalize(text);
        if (!Rules.Contains(rule))
        {
            Rules.Add(rule);
        }

        RuleInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveRule(string rule)
    {
        Rules.Remove(rule);
    }

    /// <summary>
    /// Clears all entries of this list at once, for rebuilding a large list from scratch (auto-saves).
    /// </summary>
    [RelayCommand]
    private void ClearRules()
    {
        Rules.Clear();
    }

    /// <summary>A suggested file name when exporting this list.</summary>
    public string SuggestedFileName => string.IsNullOrWhiteSpace(Name) ? "routing.txt" : $"{Name.Trim()}-routing.txt";

    /// <summary>
    /// Serialises this list (name + rules) to a portable blob for copy / save / QR - the same share flow a
    /// config has.
    /// </summary>
    public string BuildTransferPayload() => PortableTransfer.EncodeRouting(Name, Rules);

    /// <summary>
    /// Replaces this list's name + rules from an imported blob. Returns whether the text was a recognisable
    /// routing-list blob; the result auto-saves.
    /// </summary>
    public bool ApplyImport(string text)
    {
        if (!PortableTransfer.TryDecodeRouting(text, out var name, out var importedRules))
        {
            StatusMessage = "Не похоже на список маршрутизации.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            Name = name;
        }

        Rules.Clear();
        foreach (var rule in importedRules)
        {
            Rules.Add(rule);
        }

        StatusMessage = Name.Trim().Length == 0
            ? $"Импортировано правил: {importedRules.Count}. Введите имя, чтобы сохранить."
            : $"Импортировано правил: {importedRules.Count}.";
        return true;
    }

    partial void OnNameChanged(string value)
    {
        ScheduleAutoSave();
    }

    // (Re)start the debounce so a save fires a short while after the last edit. No-op while seeding.
    private void ScheduleAutoSave()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async void OnAutoSaveTick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        await AutoSaveAsync();
    }

    private async Task AutoSaveAsync()
    {
        // Nothing to persist until the list has a name; stay quiet (no nag) while it is still blank.
        if (Name.Trim().Length == 0)
        {
            return;
        }

        // A save is still in flight: retry after the debounce so the latest edit is not dropped.
        if (IsBusy)
        {
            ScheduleAutoSave();
            return;
        }

        if (await SaveAsync())
        {
            _saveFailures = 0;
            _onSaved?.Invoke(_id);
        }
        else if (++_saveFailures <= 2)
        {
            // A transient agent rejection (the blank-name case already returned above): retry shortly so a
            // single NAK does not strand the edit unpersisted.
            ScheduleAutoSave();
        }
    }

    private static string Normalize(string text)
    {
        if (text.Contains(':'))
        {
            return text;
        }

        return text.Contains('/') ? $"cidr:{text}" : $"domain:{text}";
    }
}
