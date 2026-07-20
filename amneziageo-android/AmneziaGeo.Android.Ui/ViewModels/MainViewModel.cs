using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Android.Ui.ViewModels;

/// <summary>
/// Main view state.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    private bool _connected;

    /// <summary>
    /// Connection status text.
    /// </summary>
    public string Status => Connected ? "Connected" : "Disconnected";

    /// <summary>
    /// Toggle button caption.
    /// </summary>
    public string ActionLabel => Connected ? "Disconnect" : "Connect";

    partial void OnConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(Status));
    }

    [RelayCommand]
    private void ToggleConnection()
    {
        Connected = !Connected;
    }
}