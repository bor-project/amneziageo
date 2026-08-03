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
        if (!e.Handled && IsEffectivelyEnabled && SelectPresenter is { } presenter)
        {
            // Space opens on release, like a button. Opening on press leaves its key-up for the sheet row that
            // has just taken focus, and that row picks itself: the sheet blinks and closes. A D-pad centre
            // press on Android arrives as Space.
            if (e.Key is Key.Space)
            {
                e.Handled = true;
                return;
            }

            if (OpensSelect(e))
            {
                e.Handled = true;
                presenter(this);
                return;
            }
        }

        base.OnKeyDown(e);
    }

    /// <inheritdoc/>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!e.Handled && IsEffectivelyEnabled && e.Key is Key.Space && SelectPresenter is { } presenter)
        {
            e.Handled = true;
            presenter(this);
            return;
        }

        base.OnKeyUp(e);
    }

    // The keys that replace the built-in popup with the platform select presenter.
    private static bool OpensSelect(KeyEventArgs e)
        => e.Key is Key.Enter or Key.F4
            || (e.Key is Key.Down or Key.Up && e.KeyModifiers.HasFlag(KeyModifiers.Alt));
}
