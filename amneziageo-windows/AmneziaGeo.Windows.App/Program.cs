namespace AmneziaGeo.Windows.App;

/// <summary>
/// Windows host entry point.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        return AppEntry.RunAsync(args);
    }
}
