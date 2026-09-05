using System.ComponentModel;
using System.Globalization;
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
    private readonly MainWindowViewModel _shell;

    // Кто был основным до замера, кого он занял и с какой ролью взял.
    private (string Name, int Slot) _lent = (string.Empty, TunnelRoles.Aside);

    /// <summary>
    /// ctor
    /// </summary>
    public FleetConnectionViewModel(MainWindowViewModel host, IAgentConnection connection, UiPreferences prefs)
        : base(host, connection, prefs)
    {
        _link = connection;
        _shell = host;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLinkStatus))]
    [NotifyPropertyChangedFor(nameof(ShowLinkSpeed))]
    private bool _multiServer;

    [ObservableProperty]
    private string _primary = string.Empty;

    /// <summary>
    /// Заперты ли места: пока замер держит машину, их не двигают.
    /// </summary>
    [ObservableProperty]
    private bool _rolesLocked;

    /// <summary>
    /// В режиме выбор только выбирает: туннель поднимают кнопкой карточки или шапкой.
    /// </summary>
    protected override bool MovesWithSelection => !MultiServer;

    /// <inheritdoc/>
    protected override bool KeepsAsked => MultiServer;

    /// <summary>
    /// Числа туннеля в режиме стоят на строке своего сервера, а не под кнопкой.
    /// </summary>
    public override bool ShowLinkStatus => !MultiServer && base.ShowLinkStatus;

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
        await DialAsync(item);
    }

    /// <inheritdoc/>
    protected override async Task ToggleConnection()
    {
        if (!MultiServer)
        {
            await base.ToggleConnection();
            return;
        }

        // Кнопка в шапке - рубильник машины: снимает весь набор разом и поднимает его тем же составом.
        // Отдельный сервер водит его карточка.
        MarkSet(!IsTunnelActive);
        await base.ToggleConnection();
    }

    // Ход рубильника виден на карточках сразу: снимок дойдёт до них позже команды. Снимаются те, что стоят,
    // поднимаются те, которыми набор помнит себя.
    private void MarkSet(bool up)
    {
        var catalogue = _shell.ConfigFleet;
        if (catalogue is null)
        {
            return;
        }

        foreach (var row in catalogue.Configs)
        {
            if (row is not FleetConfigItemViewModel card)
            {
                continue;
            }

            var standing = card.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;
            if (up && catalogue.Resume.Contains(card.Name, StringComparer.Ordinal))
            {
                card.Mark(ConnectionStatus.Connecting);
            }
            else if (!up && standing)
            {
                card.Mark(ConnectionStatus.Disconnecting);
            }
        }
    }

    // Поднимает или снимает один сервер, ведя на время команды и карточку, и шапку.
    private async Task DialAsync(ConfigItemViewModel item)
    {
        var up = item.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;
        var going = up ? ConnectionStatus.Disconnecting : ConnectionStatus.Connecting;
        var back = up ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
        var card = item as FleetConfigItemViewModel;
        card?.Mark(going);
        Head(item, !up, going);
        ToggleInFlight = true;
        try
        {
            var ack = await _link.SendCommandAsync(new IpcCommand(up ? FleetOps.Disconnect : FleetOps.Connect, [item.Name]));
            if (ack.Ok)
            {
                return;
            }

            card?.Settle(back);
            Head(item, up, back);
            if (!up && OwnedByOtherAck(ack))
            {
                RequestTakeover();
                return;
            }

            ShowNotice(FleetNotice.Of(ack));
        }
        finally
        {
            // Карточку снимку не возвращаем: она держит своё состояние, пока снимок не узнает о команде.
            ToggleInFlight = false;
        }
    }

    // Шапка отвечает за выбранный сервер, поэтому его команда ведёт и её.
    private void Head(ConfigItemViewModel item, bool active, string status)
    {
        if (!string.Equals(item.Name, ActiveConfig?.Name, StringComparison.Ordinal))
        {
            return;
        }

        IsTunnelActive = active;
        BoundStatus = status;
    }

    /// <inheritdoc/>
    internal override Task<bool> EnsureDisconnectedAsync(string name)
    {
        // Снесённый сервер агент убирает из набора сам, а шапка тут ведёт весь набор, и трогать его ради одной
        // конфигурации нельзя.
        return MultiServer ? Task.FromResult(true) : base.EnsureDisconnectedAsync(name);
    }

    /// <inheritdoc/>
    public override async Task ReconnectLiveAsync(ConfigItemViewModel item)
    {
        if (!MultiServer)
        {
            await base.ReconnectLiveAsync(item);
            return;
        }

        // Правка транспорта доходит до туннеля новым подъёмом, и карточка поднимает свой сервер.
        if (item.Status is not (ConnectionStatus.Connected or ConnectionStatus.Connecting))
        {
            return;
        }

        var ack = await _link.SendCommandAsync(new IpcCommand(FleetOps.Disconnect, [item.Name]));
        if (!ack.Ok)
        {
            ShowNotice(FleetNotice.Of(ack));
            return;
        }

        await WaitForMemberDownAsync(item);
        ack = await _link.SendCommandAsync(new IpcCommand(FleetOps.Connect, [item.Name]));
        if (!ack.Ok)
        {
            ShowNotice(FleetNotice.Of(ack));
        }
    }

    // Ждёт ухода сервера, но не дольше пятнадцати секунд.
    private static async Task WaitForMemberDownAsync(ConfigItemViewModel item)
    {
        for (var at = 0; at < 75 && item.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting or ConnectionStatus.Disconnecting; at++)
        {
            await Task.Delay(200);
        }
    }

    /// <summary>
    /// Отдаёт машину выбранному серверу на время замера.
    /// </summary>
    internal async Task TakePrimaryAsync()
    {
        if (!MultiServer || ActiveConfig is not { } row || string.Equals(row.Name, Primary, StringComparison.Ordinal))
        {
            return;
        }

        // Где сервер стоял, читается до запроса: ответ на него уже переставляет цепочку.
        var lent = (row.Name, row is FleetConfigItemViewModel card ? card.Slot : TunnelRoles.Lead);
        var ack = await _link.SendCommandAsync(new IpcCommand(FleetOps.SetPrimary, [row.Name]));
        if (!ack.Ok)
        {
            ShowNotice(FleetNotice.Of(ack));
            return;
        }

        _lent = lent;
    }

    /// <summary>
    /// Возвращает машину тому, кто был основным до замера.
    /// </summary>
    internal async Task ReturnPrimaryAsync()
    {
        var lent = _lent;
        _lent = (string.Empty, TunnelRoles.Aside);
        if (lent.Name.Length == 0)
        {
            return;
        }

        // Занятый встаёт на своё место, и цепочка за ним смыкается как была.
        var ack = await _link.SendCommandAsync(new IpcCommand(FleetOps.SetSlot,
            [lent.Name, lent.Slot.ToString(CultureInfo.InvariantCulture)]));
        if (!ack.Ok)
        {
            ShowNotice(FleetNotice.Of(ack));
        }
    }

    partial void OnMultiServerChanged(bool value)
    {
        ConnectConfigCommand.NotifyCanExecuteChanged();
    }

    partial void OnRolesLockedChanged(bool value)
    {
        _shell.ConfigFleet?.NotifyRoleGate();
    }
}
