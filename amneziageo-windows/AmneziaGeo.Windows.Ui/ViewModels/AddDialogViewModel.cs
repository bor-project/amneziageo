using System.Collections.ObjectModel;
using System.Diagnostics;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// View model for the add dialog: import a config, define a balancer, or restore a backup.
/// </summary>
internal sealed partial class AddDialogViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string? _configPath;

    [ObservableProperty]
    private string _configName = string.Empty;

    [ObservableProperty]
    private string _balancerName = string.Empty;

    [ObservableProperty]
    private string _recheckSeconds = "60";

    [ObservableProperty]
    private string _balancerMode = "priority";

    [ObservableProperty]
    private string? _restorePath;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public AddDialogViewModel(AgentConnection connection, IReadOnlyList<string> configNames)
    {
        _connection = connection;
        foreach (var name in configNames)
        {
            Members.Add(new MemberChoice(name));
        }
    }

    /// <summary>
    /// The available configs that can join a balancer.
    /// </summary>
    public ObservableCollection<MemberChoice> Members { get; } = [];

    /// <summary>
    /// The selectable balancer modes.
    /// </summary>
    public IReadOnlyList<string> Modes { get; } = ["priority", "latency"];

    /// <summary>
    /// Runs the action for the selected tab; returns whether the dialog should close.
    /// </summary>
    public async Task<bool> ConfirmAsync()
    {
        return SelectedTabIndex switch
        {
            0 => await AddConfigAsync(),
            1 => await AddBalancerAsync(),
            _ => false,
        };
    }

    /// <summary>
    /// Launches an elevated restore of the selected backup; returns whether it started.
    /// </summary>
    public bool TryStartRestore()
    {
        if (string.IsNullOrWhiteSpace(RestorePath))
        {
            StatusMessage = "Выберите файл бэкапа";
            return false;
        }

        var app = AppLocator.Locate();
        if (app is null)
        {
            StatusMessage = "Не найден AmneziaGeo.Windows.App.exe";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(app, $"restore \"{RestorePath}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
    }

    private async Task<bool> AddConfigAsync()
    {
        var path = ConfigPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "Выберите файл конфигурации";
            return false;
        }

        var name = string.IsNullOrWhiteSpace(ConfigName) ? Path.GetFileNameWithoutExtension(path) : ConfigName.Trim();
        return await SendAsync(new IpcCommand(IpcContract.OpAddConfig, [name, path]));
    }

    private async Task<bool> AddBalancerAsync()
    {
        if (string.IsNullOrWhiteSpace(BalancerName))
        {
            StatusMessage = "Введите имя балансировщика";
            return false;
        }

        var members = Members.Where(member => member.IsSelected).Select(member => member.Name).ToList();
        if (members.Count == 0)
        {
            StatusMessage = "Выберите хотя бы один сервер";
            return false;
        }

        var args = new List<string> { BalancerName.Trim(), RecheckSeconds.Trim(), BalancerMode };
        args.AddRange(members);
        return await SendAsync(new IpcCommand(IpcContract.OpAddBalancer, args));
    }

    private async Task<bool> SendAsync(IpcCommand command)
    {
        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(command);
            StatusMessage = ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
