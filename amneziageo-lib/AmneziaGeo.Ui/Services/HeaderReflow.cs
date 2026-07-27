using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Раскладка шапки «сегменты + комбобокс выбора»: рядом когда влезает, комбобокс на своей строке когда тесно.
/// Капшен над комбобоксом плавающий (нулевая высота), поэтому комбобокс держится по центру строки с табами.
/// </summary>
internal sealed class HeaderReflow
{
    // Зазор между табами и комбобоксом при раскладке в одну строку.
    private const double Gap = 24;
    private const double PickerWidth = 160;

    private readonly Grid _host;
    private readonly Control _tabs;
    private readonly Panel _picker;
    private readonly Control _field;
    private readonly Control _floatLabel;
    private readonly Control _inlineLabel;
    private readonly Func<bool> _compact;

    /// <summary>
    /// ctor
    /// </summary>
    public HeaderReflow(Grid host, Control tabs, Panel picker, Control field, Control floatLabel, Control inlineLabel, Func<bool> compact)
    {
        _host = host;
        _tabs = tabs;
        _picker = picker;
        _field = field;
        _floatLabel = floatLabel;
        _inlineLabel = inlineLabel;
        _compact = compact;
        _host.PropertyChanged += OnHostPropertyChanged;
        Apply();
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty)
        {
            Apply();
        }
    }

    /// <summary>
    /// Пересчитывает раскладку по текущей ширине шапки и флагу мобильного режима.
    /// </summary>
    public void Apply()
    {
        var stacked = _compact() || _host.Bounds.Width < _tabs.Bounds.Width + PickerWidth + Gap;

        Grid.SetRow(_picker, stacked ? 1 : 0);
        Grid.SetColumn(_picker, stacked ? 0 : 1);
        Grid.SetColumnSpan(_picker, stacked ? 2 : 1);
        _picker.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        _picker.VerticalAlignment = stacked ? VerticalAlignment.Top : VerticalAlignment.Center;
        _picker.MaxWidth = stacked ? double.PositiveInfinity : 260;
        _picker.Margin = stacked ? new Thickness(0, 14, 0, 0) : default;
        _field.Width = stacked ? double.NaN : PickerWidth;
        _floatLabel.IsVisible = !stacked;
        _inlineLabel.IsVisible = stacked;
    }
}
