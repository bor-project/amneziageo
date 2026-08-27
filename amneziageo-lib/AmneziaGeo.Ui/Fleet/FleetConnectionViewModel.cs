using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Главный экран, пока машина держит несколько туннелей: шапка ведёт выбранный сервер, а карточки - каждая
/// свой.
/// </summary>
internal sealed partial class FleetConnectionViewModel : ConnectionViewModel
{
    private readonly IAgentConnection _link;

    /// <summary>
    /// ctor
    /// </summary>
    public FleetConnectionViewModel(MainWindowViewModel host, IAgentConnection connection, UiPreferences prefs)
        : base(host, connection, prefs)
    {
        _link = connection;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMakePrimary))]
    [NotifyCanExecuteChangedFor(nameof(MakePrimaryCommand))]
    private bool _multiServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMakePrimary))]
    [NotifyCanExecuteChangedFor(nameof(MakePrimaryCommand))]
    private string _primary = string.Empty;

    /// <summary>
    /// Можно ли отдать машину выбранному серверу: он уже основной - нечего и просить.
    /// </summary>
    public bool CanMakePrimary => MultiServer
        && IsConnected
        && ActiveConfig is { } row
        && !string.Equals(row.Name, Primary, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override void Apply(StatusSnapshot snapshot)
    {
        // Режим читается до шапки: по нему карточки берут состояние из снимка, а не с кнопки.
        MultiServer = snapshot.MultiServer;
        Primary = snapshot.Fleet?.Primary ?? string.Empty;
        base.Apply(snapshot);
    }

    /// <inheritdoc/>
    protected override void PushCardPower()
    {
        // В режиме каждая карточка показывает свой туннель, и состояние ей приносит снимок.
        if (!MultiServer)
        {
            base.PushCardPower();
        }
    }

    /// <inheritdoc/>
    protected override bool CanDialConfig(ConfigItemViewModel? item)
    {
        // Кнопка карточки в режиме ждёт только агента: серверы поднимаются и падают порознь.
        return MultiServer ? item is not null && IsConnected : base.CanDialConfig(item);
    }

    /// <inheritdoc/>
    protected override async Task ConnectConfig(ConfigItemViewModel? item)
    {
        if (!MultiServer)
        {
            await base.ConnectConfig(item);
            return;
        }

        if (item is null)
        {
            return;
        }

        // Карточка водит свой сервер и только его: живые туннели остальных остаются стоять.
        var up = item.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;
        var ack = await _link.SendCommandAsync(new IpcCommand(up ? FleetOps.Disconnect : FleetOps.Connect, [item.Name]));
        if (ack.Ok)
        {
            return;
        }

        if (!up && OwnedByOtherAck(ack))
        {
            RequestTakeover();
            return;
        }

        ShowNotice(FleetNotice.Of(ack));
    }

    // Отдаёт машину выбранному серверу.
    [RelayCommand(CanExecute = nameof(CanMakePrimary))]
    private async Task MakePrimary()
    {
        if (ActiveConfig is not { } row)
        {
            return;
        }

        var ack = await _link.SendCommandAsync(new IpcCommand(FleetOps.SetPrimary, [row.Name]));
        if (!ack.Ok)
        {
            ShowNotice(FleetNotice.Of(ack));
        }
    }

    partial void OnMultiServerChanged(bool value)
    {
        ConnectConfigCommand.NotifyCanExecuteChanged();
    }
}
