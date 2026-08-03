using System.Collections.Generic;
using System.Threading.Tasks;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Shows a built-in file browser and reports the chosen path, or null when the user backs out.
/// </summary>
internal delegate Task<string?> FileBrowserRequest(string title, IReadOnlyList<string> extensions);

/// <summary>
/// Runtime registry of the built-in file browser. A host registers one when the platform has no document
/// picker (Android TV); screens then browse through it instead of the system picker.
/// </summary>
internal static class FileBrowserHost
{
    private static FileBrowserRequest? _browser;

    /// <summary>
    /// Whether this platform supplies a built-in browser.
    /// </summary>
    public static bool IsAvailable => _browser is not null;

    /// <summary>
    /// Registers the platform browser.
    /// </summary>
    public static void Register(FileBrowserRequest browser) => _browser = browser;

    /// <summary>
    /// Starts a browse, or returns null when the platform has no browser.
    /// </summary>
    public static Task<string?>? BrowseAsync(string title, IReadOnlyList<string> extensions)
        => _browser?.Invoke(title, extensions);
}
