using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.ViewModels;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Карточка сервера, пока машина держит несколько туннелей: та же карточка и место туннеля на ней.
/// </summary>
internal sealed partial class FleetConfigItemViewModel : ConfigItemViewModel
{
    private readonly FleetConfigViewModel _catalogue;

    /// <summary>
    /// ctor
    /// </summary>
    public FleetConfigItemViewModel(FleetConfigViewModel catalogue)
    {
        _catalogue = catalogue;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrimary))]
    [NotifyPropertyChangedFor(nameof(SlotText))]
    private int _slot = TunnelRoles.Aside;

    /// <summary>
    /// Просят ли этот сервер держать поднятым.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardStatusBrush))]
    private bool _wanted;

    /// <summary>
    /// Пока сервер заявлен, но ещё не поднят, точка носит цвет попытки: набор его добивается.
    /// </summary>
    public override IBrush CardStatusBrush =>
        Wanted && Status is not ConnectionStatus.Connected ? PowerAmber : base.CardStatusBrush;

    // Сколько карточка держит своё состояние, если снимок так и не узнал о команде.
    private const long DialWait = 15000;

    private bool _dialingUp;
    private long _dialingUntil;

    /// <summary>
    /// Ведёт ли карточка свою команду.
    /// </summary>
    internal bool Dialing { get; private set; }

    // Ставит карточке состояние на время команды.
    internal void Mark(string status)
    {
        Dialing = true;
        _dialingUp = string.Equals(status, ConnectionStatus.Connecting, StringComparison.Ordinal);
        _dialingUntil = Environment.TickCount64 + DialWait;
        Status = status;
    }

    // Обрывает ожидание и ставит названное состояние.
    internal void Settle(string status)
    {
        Dialing = false;
        Status = status;
    }

    /// <summary>
    /// Берёт ли карточка состояние из снимка. Пока команда в пути, снимок принимается только когда он уже знает
    /// о ней: снятый до неё вернул бы карточку к прежнему виду, и она мигнула бы.
    /// </summary>
    internal bool Accepts(string reported)
    {
        if (!Dialing)
        {
            return true;
        }

        if (!Knows(reported) && Environment.TickCount64 < _dialingUntil)
        {
            return false;
        }

        Dialing = false;
        return true;
    }

    // Знает ли снимок о команде: поднимаемый сервер отвечает подъёмом или отказом, снимаемый - снятием.
    private bool Knows(string reported)
    {
        return _dialingUp
            ? reported is ConnectionStatus.Connecting or ConnectionStatus.Connected or ConnectionStatus.Failed or ConnectionStatus.Preempted
            : reported is ConnectionStatus.Disconnecting or ConnectionStatus.Disconnected or ConnectionStatus.Idle or ConnectionStatus.Failed;
    }

    /// <summary>
    /// Несёт ли сервер весь прочий трафик машины.
    /// </summary>
    public bool IsPrimary => Slot == TunnelRoles.Lead;

    /// <summary>
    /// Место на бейдже.
    /// </summary>
    public string SlotText => Word(Slot);

    /// <summary>
    /// Чему бейдж принадлежит.
    /// </summary>
    public string SlotLabel => Loc.Instance.Get("Main_CardTunnelLabel");

    /// <summary>
    /// Места, на которые ставят сервер: вся цепочка, а стоящему вне её - ещё одно место в её конце.
    /// </summary>
    public IReadOnlyList<TunnelSlotChoice> SlotChoices
    {
        get
        {
            var places = _catalogue.ChainLength + (Slot == TunnelRoles.Aside ? 1 : 0);
            var choices = new List<TunnelSlotChoice>(places + 1);
            for (var slot = TunnelRoles.Lead; slot <= places; slot++)
            {
                choices.Add(new TunnelSlotChoice(slot, Word(slot)));
            }

            choices.Add(new TunnelSlotChoice(TunnelRoles.Aside, Word(TunnelRoles.Aside)));
            return choices;
        }
    }

    /// <summary>
    /// Открыт ли бейдж: пока замер держит машину, места не меняют.
    /// </summary>
    public bool RolesFree => _catalogue.RolesFree;

    /// <summary>
    /// Пересчитывает запор мест на карточке.
    /// </summary>
    internal void RefreshRoleGate()
    {
        OnPropertyChanged(nameof(RolesFree));
        SetSlotCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc/>
    public override void RefreshLocalizedLabels()
    {
        base.RefreshLocalizedLabels();
        OnPropertyChanged(nameof(SlotLabel));
        OnPropertyChanged(nameof(SlotText));
    }

    // Ставит сервер на место в цепочке.
    [RelayCommand(CanExecute = nameof(CanSetSlot))]
    private async Task SetSlot(int slot)
    {
        if (slot != Slot)
        {
            await _catalogue.SetSlotAsync(Name, slot);
        }
    }

    // Места меняют, пока их никто не держит.
    private bool CanSetSlot(int slot)
    {
        return _catalogue.RolesFree;
    }

    // Место словами: первое несёт машину, дальше резерв по порядку, вне цепочки - нейтральный.
    private static string Word(int slot)
    {
        return slot switch
        {
            TunnelRoles.Lead => Loc.Instance.Get("Main_RolePrimary"),
            <= TunnelRoles.Aside => Loc.Instance.Get("Main_RoleNeutral"),
            _ => Loc.Instance.Get("Main_SlotReserve", (slot - 1).ToString(CultureInfo.InvariantCulture)),
        };
    }
}
