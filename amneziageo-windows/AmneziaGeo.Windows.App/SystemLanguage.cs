using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The language Windows shows its own interface in: the agent reads no cultures, so it asks Windows.
/// </summary>
internal static class SystemLanguage
{
    private const ushort Russian = 0x19;

    /// <summary>
    /// Returns the letters of the language: ru, or en for any other.
    /// </summary>
    public static string Letters() => (GetSystemDefaultUILanguage() & 0x3ff) == Russian ? "ru" : "en";

    [DllImport("kernel32.dll")]
    private static extern ushort GetSystemDefaultUILanguage();
}
