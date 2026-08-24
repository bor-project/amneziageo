using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One server in the auto-switching list: its place in the order, whether it carries the default route right now
/// and whether switching walks it at all.
/// </summary>
internal sealed partial class FailoverRowViewModel : ViewModelBase
{
    private readonly FailoverViewModel _owner;

    /// <summary>
    /// ctor
    /// </summary>
    public FailoverRowViewModel(string name, FailoverViewModel owner)
    {
        Name = name;
        _owner = owner;
    }

    /// <summary>
    /// Configuration this row stands for.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Place in the order, counted from one.
    /// </summary>
    [ObservableProperty]
    private int _number;

    /// <summary>
    /// Whether this server carries the default route right now.
    /// </summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>
    /// Whether auto-switching walks this server.
    /// </summary>
    [ObservableProperty]
    private bool _participates = true;

    /// <summary>
    /// Whether the arrows move this row instead of walking to the next one.
    /// </summary>
    [ObservableProperty]
    private bool _isMoving;

    /// <summary>
    /// Moves the row by one place.
    /// </summary>
    public void Step(int delta)
    {
        _owner.MoveRow(this, delta);
    }

    /// <summary>
    /// Leaves the move mode, storing the order the row was left in.
    /// </summary>
    public void EndMove()
    {
        _owner.EndMove(this);
    }

    // Enters or leaves the move mode.
    [RelayCommand]
    private void ToggleMove()
    {
        _owner.ToggleMove(this);
    }

    // Stores the order a drag left the list in.
    [RelayCommand]
    private void Drop()
    {
        _owner.SaveOrder();
    }

    partial void OnParticipatesChanged(bool value)
    {
        _owner.PushSkipped();
    }
}
