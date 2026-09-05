using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Настройки туннеля, пока машина держит несколько туннелей: под переключателем режима встают числа, по
/// которым пересматривается лучший сервер. При выключенном режиме их нет.
/// </summary>
internal sealed partial class FleetConnectionsViewModel : ConnectionsViewModel
{
    // Взведён, пока числа берутся из снимка: иначе они тут же уедут обратно в агент.
    private bool _seeding;

    /// <summary>
    /// ctor
    /// </summary>
    public FleetConnectionsViewModel(IAgentConnection connection)
        : base(connection)
    {
    }

    /// <summary>
    /// Как часто пересматривается лучший сервер, в секундах.
    /// </summary>
    [ObservableProperty]
    private int _balanceIntervalSeconds = BalancePolicy.Default.IntervalSeconds;

    /// <summary>
    /// Сколько молчаливых проверок подряд сервер держит то, что на нём едет.
    /// </summary>
    [ObservableProperty]
    private int _balanceStrikes = BalancePolicy.Default.Strikes;

    /// <summary>
    /// В какую долю отклика текущего сервера другой должен уложиться, чтобы забрать правила, в процентах.
    /// </summary>
    [ObservableProperty]
    private int _balanceMarginPercent = BalancePolicy.Default.MarginPercent;

    /// <inheritdoc/>
    public override object? TunnelExtras => this;

    /// <inheritdoc/>
    public override void Apply(StatusSnapshot snapshot)
    {
        base.Apply(snapshot);

        var policy = snapshot.Fleet?.Balance ?? BalancePolicy.Default;
        _seeding = true;
        BalanceIntervalSeconds = policy.IntervalSeconds;
        BalanceStrikes = policy.Strikes;
        BalanceMarginPercent = policy.MarginPercent;
        _seeding = false;
        OnPropertyChanged(nameof(TunnelExtras));
    }

    partial void OnBalanceIntervalSecondsChanged(int value)
    {
        Push("balance-interval-seconds", value);
    }

    partial void OnBalanceStrikesChanged(int value)
    {
        Push("balance-strikes", value);
    }

    partial void OnBalanceMarginPercentChanged(int value)
    {
        Push("balance-margin-percent", value);
    }

    // Полупустое поле числом не считается: агент получает только то, что уже осмысленно.
    private void Push(string key, int value)
    {
        if (!_seeding && value > 0)
        {
            _ = SetSettingAsync(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
