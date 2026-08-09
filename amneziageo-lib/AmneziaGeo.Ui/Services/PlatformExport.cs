using System.Threading.Tasks;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Hands exported bytes to another application and reports whether the chooser opened.
/// </summary>
internal delegate Task<bool> ExportSendRequest(string name, string mime, byte[] content);

/// <summary>
/// Puts a picture on the system clipboard and reports whether it landed there.
/// </summary>
internal delegate Task<bool> ExportImageRequest(string name, byte[] png);

/// <summary>
/// Runtime registry of the platform export paths. Android registers both: it hands a file to the system
/// chooser, and its clipboard carries a picture only as a link to one.
/// </summary>
internal static class PlatformExportHost
{
    private static ExportSendRequest? _send;
    private static ExportImageRequest? _copyImage;

    /// <summary>
    /// Whether this platform sends an export to another application.
    /// </summary>
    public static bool CanSend => _send is not null;

    /// <summary>
    /// Registers the platform paths.
    /// </summary>
    public static void Register(ExportSendRequest send, ExportImageRequest copyImage)
    {
        _send = send;
        _copyImage = copyImage;
    }

    /// <summary>
    /// Sends the export, or returns null when the platform has no such path.
    /// </summary>
    public static Task<bool>? SendAsync(string name, string mime, byte[] content) => _send?.Invoke(name, mime, content);

    /// <summary>
    /// Copies the picture, or returns null when the toolkit clipboard takes one on this platform.
    /// </summary>
    public static Task<bool>? CopyImageAsync(string name, byte[] png) => _copyImage?.Invoke(name, png);
}
