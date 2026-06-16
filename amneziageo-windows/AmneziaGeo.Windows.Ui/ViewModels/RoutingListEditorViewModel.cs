using System.Collections.ObjectModel;
using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Editor for a shared routing list: name + rules (geo categories or manual domains / cidrs).
/// Changes auto-save (debounced) through the agent — there is no explicit save button.
/// </summary>
internal sealed partial class RoutingListEditorViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly Action<long>? _onSaved;
    private readonly DispatcherTimer _saveTimer;
    private long _id;

    // Suppresses auto-save while the editor is being populated (ctor + LoadAsync), so loading a list
    // doesn't immediately re-save it.
    private bool _suppressAutoSave = true;

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
    {
        _connection = connection;
        _onSaved = onSaved;
        _id = 0;
        IsNew = true;
        _saveTimer = CreateSaveTimer();
    }

    /// <summary>
    /// ctor used when editing an existing routing list (rules loaded via LoadAsync).
    /// </summary>
    public RoutingListEditorViewModel(AgentConnection connection, long id, string name, Action<long>? onSaved = null)
    {
        _connection = connection;
        _onSaved = onSaved;
        _id = id;
        _saveTimer = CreateSaveTimer();
        Name = name;
        IsNew = false;
    }

    private DispatcherTimer CreateSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = AutoSaveAsync();
        };
        return timer;
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

        // Loading is complete: let subsequent edits auto-save.
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
    /// Stops a pending auto-save; called when this editor is discarded so a queued save does not fire
    /// against a closed editor.
    /// </summary>
    public void CancelPendingSave()
    {
        _saveTimer.Stop();
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
            ScheduleSave();
        }

        RuleInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveRule(string rule)
    {
        if (Rules.Remove(rule))
        {
            ScheduleSave();
        }
    }

    partial void OnNameChanged(string value)
    {
        ScheduleSave();
    }

    // Debounce edits into a single save: each change restarts the timer, so rapid typing / multiple
    // rule edits collapse into one persist + re-materialize once the user pauses.
    private void ScheduleSave()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task AutoSaveAsync()
    {
        if (IsBusy)
        {
            // A save is already in flight; try again after it settles.
            ScheduleSave();
            return;
        }

        if (await SaveAsync())
        {
            _onSaved?.Invoke(_id);
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
