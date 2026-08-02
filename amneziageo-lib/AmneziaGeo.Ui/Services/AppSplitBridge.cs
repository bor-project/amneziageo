using System;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Hook for a platform per-app split picker. The host (Android) registers a presenter at startup; the profile
/// screen invokes it with the profile name. No registration means the platform has no per-app split.
/// </summary>
public static class AppSplitBridge
{
    private static Action<string>? _present;

    /// <summary>
    /// Whether a per-app split picker is available on this platform.
    /// </summary>
    public static bool IsAvailable => _present is not null;

    /// <summary>
    /// Registers the platform picker presenter.
    /// </summary>
    public static void Register(Action<string> present) => _present = present;

    /// <summary>
    /// Opens the picker for a profile.
    /// </summary>
    public static void Present(string profile) => _present?.Invoke(profile);
}
