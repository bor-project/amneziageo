using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The per-config bypass-exclusions editor shown on a profile's Маршрутизация aspect: the manual list
/// (domains kept on the local resolver, IP/CIDR routed direct), saved through the agent
/// (set-config-exclusions). Moved here from the former global settings so each profile carries its own.
/// The former auto-exclude-LAN flag is replaced by an explicit "add local subnets" action that fetches the
/// machine's connected subnets and merges them into the visible list. Applies on the next connect.
/// </summary>
internal sealed partial class ConfigExclusionsViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;

    [ObservableProperty]
    private string _exclusions = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigExclusionsViewModel(AgentConnection connection, string name, string exclusions)
    {
        _connection = connection;
        ConfigName = name;
        _exclusions = exclusions;
    }

    /// <summary>The configuration name being edited.</summary>
    public string ConfigName { get; }

    /// <summary>
    /// Saves the exclusions list through the agent. Applies on reconnect.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConfigExclusions,
                [ConfigName, (Exclusions ?? string.Empty).Trim()]));
            StatusMessage = ack.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fetches the machine's currently-connected local subnets from the agent and merges them into the list
    /// (no duplicates). The user reviews the result and presses «Сохранить» to persist - nothing is saved
    /// here, so the addition stays visible and editable first.
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

            // Merge into the current list, preserving its existing lines and appending only subnets not
            // already present (case-insensitive).
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
                ? $"Добавлено локальных сетей: {added.Count}. Проверьте список и нажмите «Сохранить»."
                : subnets.Length == 0
                    ? "Активные локальные сети не обнаружены."
                    : "Все локальные сети уже в списке.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
