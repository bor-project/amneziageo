using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A fact only Windows can answer. Off Windows it is skipped with the reason instead of failing, so the run on a
/// Linux build machine tells apart a test that cannot hold here from a test that broke.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    /// <summary>
    /// ctor
    /// </summary>
    public WindowsFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = $"needs Windows: {because}";
        }
    }
}
