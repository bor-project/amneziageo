using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One account of the local proxy as the editor shows it: the pair a client sends, with the password hidden until
/// it is asked for.
/// </summary>
internal sealed partial class ProxyAccountViewModel : ViewModelBase
{
    private readonly Action _changed;

    /// <summary>
    /// ctor
    /// </summary>
    public ProxyAccountViewModel(string user, string password, Action changed)
    {
        _user = user;
        _password = password;
        _changed = changed;
    }

    /// <summary>
    /// Name the client sends.
    /// </summary>
    [ObservableProperty]
    private string _user;

    /// <summary>
    /// Password that goes with the name.
    /// </summary>
    [ObservableProperty]
    private string _password;

    /// <summary>
    /// Whether the password is shown as it is written.
    /// </summary>
    [ObservableProperty]
    private bool _isRevealed;

    partial void OnUserChanged(string value)
    {
        _changed();
    }

    partial void OnPasswordChanged(string value)
    {
        _changed();
    }

    [RelayCommand]
    private void ToggleReveal()
    {
        IsRevealed = !IsRevealed;
    }
}
