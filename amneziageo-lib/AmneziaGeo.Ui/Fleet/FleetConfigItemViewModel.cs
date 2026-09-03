using System.Globalization;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.ViewModels;
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
    /// Ведёт ли карточка свою команду.
    /// </summary>
    internal bool Dialing { get; private set; }

    // Ставит карточке состояние на время команды.
    internal void Mark(string status)
    {
        Dialing = true;
        Status = status;
    }

    // Возвращает карточку снимку.
    internal void Release()
    {
        Dialing = false;
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
