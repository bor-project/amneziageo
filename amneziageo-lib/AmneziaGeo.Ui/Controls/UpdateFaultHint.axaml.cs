using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Знак главного экрана о неудачном поиске обновлений на выбранном сервере.
/// </summary>
internal sealed partial class UpdateFaultHint : UserControl
{
    private readonly PopupFlyoutBase _flyout;

    /// <summary>
    /// ctor
    /// </summary>
    public UpdateFaultHint()
    {
        InitializeComponent();
        _flyout = (PopupFlyoutBase)FlyoutBase.GetAttachedFlyout(MarkPart)!;
        _flyout.OverlayInputPassThroughElement = MarkPart;
    }

    // Мышь над знаком открывает окно с причиной.
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

        var hide = vm.HideUpdateFaultCommand;
        vm.Sheet.Show(
            vm.UpdateFaultText,
            string.Empty,
            [
                new ActionOption(Loc.Instance.Get("Main_UpdateFaultHide"), Glyphs.Close, () => hide.Execute(null)),
            ]);
    }

    // Ссылка окна прячет знак до новой ошибки.
    private void OnHideClick(object? sender, RoutedEventArgs e)
    {
        _flyout.Hide();
        if (DataContext is ConnectionViewModel vm)
        {
            vm.HideUpdateFaultCommand.Execute(null);
        }
    }
}
