using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Uses the native ComboBox popup unless a platform supplies an adaptive select presenter.
/// </summary>
internal sealed class AdaptiveComboBox : ComboBox
{
    internal static Action<AdaptiveComboBox>? SelectPresenter { get; set; }

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(ComboBox);

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var presenter = SelectPresenter;
        if (!e.Handled && !IsDropDownOpen && IsEffectivelyEnabled && presenter is not null)
        {
            // Let ComboBox clear its :pressed state without toggling the built-in popup.
            e.Handled = true;
            base.OnPointerReleased(e);
            presenter(this);
            return;
        }

        base.OnPointerReleased(e);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var opensSelect = e.Key is Key.Enter or Key.Space or Key.F4
            || (e.Key is Key.Down or Key.Up && e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        if (!e.Handled && IsEffectivelyEnabled && opensSelect && SelectPresenter is { } presenter)
        {
            e.Handled = true;
            presenter(this);
            return;
        }

        base.OnKeyDown(e);
    }
}
