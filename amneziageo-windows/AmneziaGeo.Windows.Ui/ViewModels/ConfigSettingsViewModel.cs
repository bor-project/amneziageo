using System.Collections.ObjectModel;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Per-config geo split-tunnel settings: server (full tunnel) versus custom routing.
/// </summary>
internal sealed partial class ConfigSettingsViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;

    [ObservableProperty]
    private bool _useCustom;

    [ObservableProperty]
    private string _ruleInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigSettingsViewModel(AgentConnection connection, string name, bool geoSplit, IReadOnlyList<string> rules)
    {
        _connection = connection;
        ConfigName = name;
        _useCustom = geoSplit;
        foreach (var rule in rules)
        {
            Rules.Add(rule);
        }
    }

    /// <summary>
    /// The configuration name being edited.
    /// </summary>
    public string ConfigName { get; }

    /// <summary>
    /// The split-tunnel rules (geo categories or custom domains/cidrs).
    /// </summary>
    public ObservableCollection<string> Rules { get; } = [];

    /// <summary>
    /// Geo category suggestions for the rule input, fetched from the agent.
    /// </summary>
    public ObservableCollection<string> GeoSuggestions { get; } = [];

    /// <summary>
    /// Loads the available geo categories from the agent for autocompletion.
    /// </summary>
    public async Task LoadSuggestionsAsync()
    {
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListGeo, []));
        if (!ack.Ok)
        {
            return;
        }

        foreach (var token in ack.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            GeoSuggestions.Add(token);
        }
    }

    /// <summary>
    /// Saves the settings through the agent; returns whether it succeeded.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        IsBusy = true;
        try
        {
            var args = new List<string> { ConfigName, UseCustom ? "on" : "off" };
            if (UseCustom)
            {
                args.AddRange(Rules);
            }

            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetGeo, args));
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
