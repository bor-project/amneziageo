using System;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Hook for the platform on-screen keyboard. The host (Android) registers a dismisser at startup; the shell drops
/// the keyboard before it steps back. No registration means the platform has no keyboard to drop.
/// </summary>
public static class SoftInputBridge
{
    private static Func<bool>? _dismiss;

    /// <summary>
    /// Registers the platform keyboard dismisser.
    /// </summary>
    public static void Register(Func<bool> dismiss) => _dismiss = dismiss;

    /// <summary>
    /// Drops the registered dismisser.
    /// </summary>
    public static void Unregister(Func<bool> dismiss)
    {
        if (_dismiss == dismiss)
        {
            _dismiss = null;
        }
    }

    /// <summary>
    /// Hides the keyboard, reporting whether it was up.
    /// </summary>
    public static bool Dismiss() => _dismiss?.Invoke() ?? false;
}
