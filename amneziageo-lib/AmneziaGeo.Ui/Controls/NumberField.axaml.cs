using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Reactive;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Числовое поле со стрелками: цифры набираются с клавиатуры, шаг задают кнопки в правом краю.
/// </summary>
internal sealed partial class NumberField : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<NumberField, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<NumberField, int>(nameof(Minimum));

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<NumberField, int>(nameof(Maximum), defaultValue: int.MaxValue);

    public static readonly StyledProperty<int> StepProperty =
        AvaloniaProperty.Register<NumberField, int>(nameof(Step), defaultValue: 1);

    public static readonly StyledProperty<bool> IsInvalidProperty =
        AvaloniaProperty.Register<NumberField, bool>(nameof(IsInvalid));

    /// <summary>
    /// ctor
    /// </summary>
    public NumberField()
    {
        InitializeComponent();
        BoxPart[!!TextBox.TextProperty] = this[!!TextProperty];
        UpPart.Click += (_, _) => Shift(Step);
        DownPart.Click += (_, _) => Shift(-Step);
        this.GetObservable(MinimumProperty).Subscribe(new AnonymousObserver<int>(value => NumericInput.SetMinimum(BoxPart, value)));
        this.GetObservable(MaximumProperty).Subscribe(new AnonymousObserver<int>(value => NumericInput.SetMaximum(BoxPart, value)));
        this.GetObservable(IsInvalidProperty).Subscribe(new AnonymousObserver<bool>(value => BoxPart.Classes.Set("invalid", value)));
    }

    /// <summary>
    /// Значение поля.
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Нижняя граница значения.
    /// </summary>
    public int Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>
    /// Верхняя граница значения.
    /// </summary>
    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>
    /// Шаг стрелок.
    /// </summary>
    public int Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>
    /// Носит ли поле красную рамку незаполненного значения.
    /// </summary>
    public bool IsInvalid
    {
        get => GetValue(IsInvalidProperty);
        set => SetValue(IsInvalidProperty, value);
    }

    // Пустое поле стрелка начинает с нижней границы.
    private void Shift(int delta)
    {
        var value = int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out var current)
            ? current
            : Minimum;
        Text = Math.Clamp(value + delta, Minimum, Math.Max(Minimum, Maximum))
            .ToString(CultureInfo.InvariantCulture);
    }
}
