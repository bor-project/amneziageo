using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Знак AmneziaGeo у того, что работает только с сервером AmneziaGeo: окно говорит, что даёт сервер и какой сервер у
/// этой конфигурации.
/// </summary>
internal sealed partial class ServerMark : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ServerMark, string?>(nameof(Text));

    public static readonly StyledProperty<string?> StateProperty =
        AvaloniaProperty.Register<ServerMark, string?>(nameof(State));

    private readonly PopupFlyoutBase _flyout;

    /// <summary>
    /// ctor
    /// </summary>
    public ServerMark()
    {
        InitializeComponent();
        _flyout = (PopupFlyoutBase)FlyoutBase.GetAttachedFlyout(MarkPart)!;
        _flyout.OverlayInputPassThroughElement = MarkPart;
        Refresh();
    }

    /// <summary>
    /// Что даёт сервер AmneziaGeo.
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Какой сервер у этой конфигурации; пусто, когда сказать нечего.
    /// </summary>
    public string? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == StateProperty)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        TextPart.Text = Text;
        StatePart.Text = State;
        StatePart.IsVisible = !string.IsNullOrEmpty(State);
        AutomationProperties.SetHelpText(MarkPart, Text);
    }

    // Мышь над знаком открывает окно.
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
            if (_flyout.IsOpen)
            {
                _flyout.Hide();
            }

            _flyout.ShowMode = FlyoutShowMode.Standard;
            _flyout.ShowAt(MarkPart);
            return;
        }

        if (this.FindAncestorOfType<MainView>()?.DataContext is not MainWindowViewModel host)
        {
            return;
        }

        var said = string.IsNullOrEmpty(State) ? Text ?? string.Empty : $"{Text}\n{State}";
        host.Sheet.Show(Loc.Instance.Get("Main_ServerMarkTitle"), said, []);
    }

    // Нажатие на знак не доходит до строки, которая по нажатию переключает свой тумблер.
    private void OnMarkTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
    }
}
