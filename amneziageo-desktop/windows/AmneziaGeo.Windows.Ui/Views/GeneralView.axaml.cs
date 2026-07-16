using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Windows.Ui.Views;

/// <summary>
/// General screen view.
/// </summary>
internal sealed partial class GeneralView : UserControl
{
    private const double ReflowBreakpoint = 680;
    private const double CardGap = 7;

    /// <summary>
    /// ctor
    /// </summary>
    public GeneralView()
    {
        InitializeComponent();
        TopCards.PropertyChanged += OnTopCardsPropertyChanged;
        ReflowTopCards();
    }

    private void OnTopCardsPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty)
        {
            ReflowTopCards();
        }
    }

    // Lays the appearance and about cards side by side when wide, stacked when narrow, each at its natural height.
    private void ReflowTopCards()
    {
        var wide = TopCards.Bounds.Width >= ReflowBreakpoint;
        TopCards.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(AboutCard, wide ? 1 : 0);
        Grid.SetRow(AboutCard, wide ? 0 : 1);
        AppearanceCard.Margin = wide ? new Thickness(0, 0, CardGap, 0) : new Thickness(0, 0, 0, CardGap);
        AboutCard.Margin = wide ? new Thickness(CardGap, 0, 0, 0) : new Thickness(0, CardGap, 0, 0);
    }
}
