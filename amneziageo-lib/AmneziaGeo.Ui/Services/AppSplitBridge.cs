using System;
using System.Collections.Generic;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Hook for a platform app picker. The host (Android) registers a presenter at startup; the routing editor
/// invokes it with the currently selected packages and a callback that takes the packages the picker offered
/// and the marked ones among them. No registration means the platform offers no app picker.
/// </summary>
public static class AppSplitBridge
{
    private static Action<IReadOnlyCollection<string>, Action<IReadOnlyCollection<string>, IReadOnlyCollection<string>>>? _present;

    /// <summary>
    /// Whether an app picker is available on this platform.
    /// </summary>
    public static bool IsAvailable => _present is not null;

    /// <summary>
    /// Registers the platform picker presenter.
    /// </summary>
    public static void Register(Action<IReadOnlyCollection<string>, Action<IReadOnlyCollection<string>, IReadOnlyCollection<string>>> present) => _present = present;

    /// <summary>
    /// Opens the app picker with the current package selection, reporting the offered and the marked packages to the callback.
    /// </summary>
    public static void Present(IReadOnlyCollection<string> selected, Action<IReadOnlyCollection<string>, IReadOnlyCollection<string>> onPicked) => _present?.Invoke(selected, onPicked);
}
