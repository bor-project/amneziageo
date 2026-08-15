using System;
using System.Threading.Tasks;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// One value handed to the editor: what to call it, what it holds now, and how it is typed.
/// </summary>
internal sealed record ValueEdit(
    string? Title,
    string? Description,
    string? Watermark,
    string? Text,
    bool Multiline,
    bool Mono);

/// <summary>
/// Hook for the shell value editor. The shell registers it at startup; a setting row invokes it and takes the
/// edited text back, or null when the user steps out. No registration means nothing opens.
/// </summary>
internal static class ValueEditorHost
{
    private static Func<ValueEdit, Task<string?>>? _edit;

    /// <summary>
    /// Registers the shell editor.
    /// </summary>
    public static void Register(Func<ValueEdit, Task<string?>> edit) => _edit = edit;

    /// <summary>
    /// Opens the editor over the screen, reporting the new value or null.
    /// </summary>
    public static Task<string?> EditAsync(ValueEdit request) => _edit?.Invoke(request) ?? Task.FromResult<string?>(null);
}
