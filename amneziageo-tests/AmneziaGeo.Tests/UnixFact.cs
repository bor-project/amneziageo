using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A fact only a Unix system can answer: permissions and symbolic links of files. On Windows it is skipped with the
/// reason instead of failing.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    /// <summary>
    /// ctor
    /// </summary>
    public UnixFactAttribute(string because)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = $"needs Unix: {because}";
        }
    }
}
