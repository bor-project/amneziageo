using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Sets the encoding the output of a started program is read in.
/// </summary>
internal static partial class ChildOutput
{
    private const int Utf8Page = 65001;

    /// <summary>
    /// Reads the output of a program shipped with the application, which writes UTF-8.
    /// </summary>
    public static ProcessStartInfo Ours(ProcessStartInfo start)
    {
        return Read(start, new UTF8Encoding(false));
    }

    /// <summary>
    /// Reads the output of a console program of the system, which writes in its OEM code page.
    /// </summary>
    public static ProcessStartInfo OfSystem(ProcessStartInfo start)
    {
        return OfSystem(start, OperatingSystem.IsWindows() ? (int)GetOEMCP() : Utf8Page);
    }

    /// <summary>
    /// Reads the output of a console program of a system with the OEM code page given.
    /// </summary>
    internal static ProcessStartInfo OfSystem(ProcessStartInfo start, int oemPage)
    {
        return Page(oemPage) is { } page ? Read(start, page) : start;
    }

    /// <summary>
    /// The encoding of a code page, null for a page nothing here knows.
    /// </summary>
    internal static Encoding? Page(int page)
    {
        if (page == Utf8Page)
        {
            return new UTF8Encoding(false);
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(page) ?? Encoding.GetEncoding(page);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static ProcessStartInfo Read(ProcessStartInfo start, Encoding encoding)
    {
        if (start.RedirectStandardOutput)
        {
            start.StandardOutputEncoding = encoding;
        }

        if (start.RedirectStandardError)
        {
            start.StandardErrorEncoding = encoding;
        }

        return start;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetOEMCP();
}
