using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Карточка сервера, пока машина держит несколько туннелей: та же карточка и роль туннеля на ней.
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
    [NotifyPropertyChangedFor(nameof(RoleText))]
    private string _role = TunnelRoles.Default;

    /// <summary>
    /// Несёт ли сервер весь прочий трафик машины.
    /// </summary>
    public bool IsPrimary => string.Equals(Role, TunnelRoles.Primary, StringComparison.Ordinal);

    /// <summary>
    /// Роль на бейдже.
    /// </summary>
    public string RoleText => Role switch
    {
        TunnelRoles.Primary => PrimaryText,
        TunnelRoles.Neutral => NeutralText,
        _ => ReserveText,
    };

    /// <summary>
    /// Чему бейдж принадлежит.
    /// </summary>
    public string RoleLabel => Loc.Instance.Get("Main_CardTunnelLabel");

    /// <summary>
    /// Роли словами - по одной на кнопку поля.
    /// </summary>
    public string PrimaryText => Loc.Instance.Get("Main_RolePrimary");

    /// <inheritdoc cref="PrimaryText"/>
    public string ReserveText => Loc.Instance.Get("Main_RoleReserve");

    /// <inheritdoc cref="PrimaryText"/>
    public string NeutralText => Loc.Instance.Get("Main_RoleNeutral");

    /// <inheritdoc/>
    public override void RefreshLocalizedLabels()
    {
        base.RefreshLocalizedLabels();
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(ReserveText));
        OnPropertyChanged(nameof(NeutralText));
        OnPropertyChanged(nameof(RoleText));
    }

    // Ставит серверу роль; основным его делает отдельный запрос - основной на машине один.
    [RelayCommand]
    private async Task SetRole(string? role)
    {
        if (role is not { Length: > 0 } || string.Equals(role, Role, StringComparison.Ordinal))
        {
            return;
        }

        await _catalogue.SetRoleAsync(Name, role);
    }
}
