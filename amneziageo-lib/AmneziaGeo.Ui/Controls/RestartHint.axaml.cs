using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Знак сохранённых настроек, которые туннель примет после переподключения.
/// </summary>
internal sealed partial class RestartHint : UserControl
{
    private readonly PopupFlyoutBase _flyout;
    private bool _focused;

    /// <summary>
    /// ctor
    /// </summary>
    public RestartHint()
    {
        InitializeComponent();
        MarkPart.GotFocus += (_, _) => _focused = true;
        MarkPart.LostFocus += (_, _) => _focused = false;
        _flyout = (PopupFlyoutBase)FlyoutBase.GetAttachedFlyout(MarkPart)!;
        _flyout.OverlayInputPassThroughElement = MarkPart;
    }

    /// <summary>
    /// Кому переходит фокус, когда знак гаснет.
    /// </summary>
    public Control? FocusFallback { get; set; }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        var focused = _focused;
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || IsVisible || !focused)
        {
            return;
        }

        _focused = false;
        FocusFallback?.Focus(UiPlatform.IsTelevision ? NavigationMethod.Directional : NavigationMethod.Unspecified);
    }

    // Мышь над знаком открывает окно со ссылкой.
    private void OnMarkPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!UiPlatform.UsesActionSheets && e.Pointer.Type == PointerType.Mouse && !_flyout.IsOpen)
        {
            _flyout.ShowMode = FlyoutShowMode.TransientWithDismissOnPointerMoveAway;
            _flyout.ShowAt(MarkPart);
        }
    }

    // Нажатие открывает шторку на телефоне и ТВ, окно на десктопе.
    private void OnMarkClick(object? sender, RoutedEventArgs e)
    {
        if (!UiPlatform.UsesActionSheets)
        {
            if (!_flyout.IsOpen)
            {
                _flyout.ShowMode = FlyoutShowMode.Standard;
                _flyout.ShowAt(MarkPart);
            }

            return;
        }

        if (DataContext is not ConnectionViewModel vm)
        {
            return;
        }

        var reconnect = vm.ReconnectCommand;
        vm.Sheet.Show(
            Loc.Instance.Get("Main_RestartSheetTitle"),
            Loc.Instance.Get("Main_RestartSheetSubtitle"),
            [
                new ActionOption(Loc.Instance.Get("Main_ReconnectApplyButton"), Glyphs.Refresh, () => reconnect.Execute(null)),
                new ActionOption(Loc.Instance.Get("Main_ReconnectLaterButton"), Glyphs.Clock, () => { }),
            ]);
    }

    // Ссылка окна переподключает туннель.
    private void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        _flyout.Hide();
        if (DataContext is ConnectionViewModel vm)
        {
            vm.ReconnectCommand.Execute(null);
        }
    }
}
