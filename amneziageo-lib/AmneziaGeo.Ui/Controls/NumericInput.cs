using System;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Числовое поле: принимает только цифры и приводит значение к границам, когда фокус уходит.
/// </summary>
internal static class NumericInput
{
    /// <summary>
    /// Нижняя граница значения.
    /// </summary>
    public static readonly AttachedProperty<int> MinimumProperty =
        AvaloniaProperty.RegisterAttached<TextBox, int>("Minimum", typeof(NumericInput));

    /// <summary>
    /// Верхняя граница значения.
    /// </summary>
    public static readonly AttachedProperty<int> MaximumProperty =
        AvaloniaProperty.RegisterAttached<TextBox, int>("Maximum", typeof(NumericInput), int.MaxValue);

    static NumericInput()
    {
        MinimumProperty.Changed.AddClassHandler<TextBox>(OnBoundChanged);
        MaximumProperty.Changed.AddClassHandler<TextBox>(OnBoundChanged);
        TextBox.TextProperty.Changed.AddClassHandler<TextBox>(OnTextChanged);
    }

    /// <summary>
    /// Задаёт нижнюю границу.
    /// </summary>
    public static void SetMinimum(TextBox target, int value) => target.SetValue(MinimumProperty, value);

    /// <summary>
    /// Читает нижнюю границу.
    /// </summary>
    public static int GetMinimum(TextBox target) => target.GetValue(MinimumProperty);

    /// <summary>
    /// Задаёт верхнюю границу.
    /// </summary>
    public static void SetMaximum(TextBox target, int value) => target.SetValue(MaximumProperty, value);

    /// <summary>
    /// Читает верхнюю границу.
    /// </summary>
    public static int GetMaximum(TextBox target) => target.GetValue(MaximumProperty);

    private static void OnBoundChanged(TextBox target, AvaloniaPropertyChangedEventArgs e)
    {
        TextInputOptions.SetContentType(target, TextInputContentType.Number);
        target.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        target.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        target.LostFocus -= OnLostFocus;
        target.LostFocus += OnLostFocus;
    }

    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        e.Handled = e.Text is { Length: > 0 } text && !IsDigits(text);
    }

    // Вставка и правка мимо клавиатуры: нецифры отсеиваются уже из текста.
    private static void OnTextChanged(TextBox target, AvaloniaPropertyChangedEventArgs e)
    {
        if (!IsNumeric(target) || target.Text is not { } text || IsDigits(text))
        {
            return;
        }

        var digits = Digits(text);
        target.Text = digits;
        target.CaretIndex = digits.Length;
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox target)
        {
            return;
        }

        var text = target.Text ?? string.Empty;
        var value = Bound(text, GetMinimum(target), GetMaximum(target)).ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(text, value, StringComparison.Ordinal))
        {
            target.Text = value;
        }
    }

    // Пустое поле берёт нижнюю границу, число длиннее разрядной сетки - верхнюю.
    private static int Bound(string text, int min, int max)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return Math.Clamp(value, min, Math.Max(min, max));
        }

        return text.Length > 0 ? max : min;
    }

    private static bool IsNumeric(TextBox target) => target.IsSet(MinimumProperty) || target.IsSet(MaximumProperty);

    private static bool IsDigits(string text)
    {
        foreach (var symbol in text)
        {
            if (!char.IsAsciiDigit(symbol))
            {
                return false;
            }
        }

        return true;
    }

    private static string Digits(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var symbol in text)
        {
            if (char.IsAsciiDigit(symbol))
            {
                builder.Append(symbol);
            }
        }

        return builder.ToString();
    }
}
