using System.Collections.ObjectModel;
using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Editor for a shared routing list: name + rules (geo categories or manual domains / cidrs).
/// </summary>
internal sealed partial class RoutingListEditorViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private long _id;

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
    public RoutingListEditorViewModel(AgentConnection connection)
    {
        _connection = connection;
        _id = 0;
        IsNew = true;
    }

    /// <summary>
    /// ctor used when editing an existing routing list (rules loaded via LoadAsync).
    /// </summary>
    public RoutingListEditorViewModel(AgentConnection connection, long id, string name)
    {
        _connection = connection;
        _id = id;
        Name = name;
        IsNew = false;
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

        if (_id == 0)
        {
            return;
        }

        var detail = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetRoutingList, [_id.ToString(CultureInfo.InvariantCulture)]));
        if (!detail.Ok)
        {
            return;
        }

        foreach (var token in detail.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Rules.Add(token);
        }
    }

    /// <summary>
    /// Saves the list (insert or update) through the agent; returns whether it succeeded.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        var trimmed = Name.Trim();
        if (trimmed.Length == 0)
        {
            StatusMessage = "Введите имя списка";
            return false;
        }

        IsBusy = true;
        try
        {
            var args = new List<string> { _id.ToString(CultureInfo.InvariantCulture), trimmed };
            args.AddRange(Rules);
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSaveRoutingList, args));
            StatusMessage = ack.Message;
            if (ack.Ok && long.TryParse(ack.Message, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resultId))
            {
                _id = resultId;
            }

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

    private static string Normalize(string text)
    {
        if (text.Contains(':'))
        {
            return text;
        }

        return text.Contains('/') ? $"cidr:{text}" : $"domain:{text}";
    }
}
