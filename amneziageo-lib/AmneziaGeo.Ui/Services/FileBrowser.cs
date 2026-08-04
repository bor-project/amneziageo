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

/// <summary>
/// Picks a save target with the built-in browser and reports the full path, or null when the user backs out.
/// </summary>
internal delegate Task<string?> FileSaveRequest(string title, string suggestedName);

/// <summary>
/// Runtime registry of the built-in save picker. A host registers one when the platform has no working save
/// dialog (Android TV ships a stub that silently does nothing); screens then save through it.
/// </summary>
internal static class FileSaverHost
{
    private static FileSaveRequest? _saver;

    /// <summary>
    /// Whether this platform supplies a built-in save picker.
    /// </summary>
    public static bool IsAvailable => _saver is not null;

    /// <summary>
    /// Registers the platform save picker.
    /// </summary>
    public static void Register(FileSaveRequest saver) => _saver = saver;

    /// <summary>
    /// Asks for a save path, or returns null when the platform has no built-in picker.
    /// </summary>
    public static Task<string?>? SaveAsync(string title, string suggestedName) => _saver?.Invoke(title, suggestedName);
}
