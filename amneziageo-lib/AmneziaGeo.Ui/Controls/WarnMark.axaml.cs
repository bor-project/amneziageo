using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Знак «!» у того, что сервер держит выключенным: окно говорит почему.
/// </summary>
internal sealed partial class WarnMark : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<WarnMark, string?>(nameof(Title));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<WarnMark, string?>(nameof(Text));

    private readonly PopupFlyoutBase _flyout;

    /// <summary>
    /// ctor
    /// </summary>
    public WarnMark()
    {
        InitializeComponent();
        _flyout = (PopupFlyoutBase)FlyoutBase.GetAttachedFlyout(MarkPart)!;
        _flyout.OverlayInputPassThroughElement = MarkPart;
        Refresh();
    }

    /// <summary>
    /// Что выключено.
    /// </summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Почему выключено и что из этого следует.
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == TextProperty)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        TitlePart.Text = Title;
        TextPart.Text = Text;
        AutomationProperties.SetName(MarkPart, Title);
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

        host.Sheet.Show(Title ?? string.Empty, Text ?? string.Empty, []);
    }

    // Нажатие на знак не доходит до карточки, которая по нажатию открывает конфигурацию.
    private void OnMarkTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
    }
}
