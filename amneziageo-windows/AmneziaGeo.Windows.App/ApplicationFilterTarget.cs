namespace AmneziaGeo.Windows.App;

/// <summary>
/// Names a file or directory in application filters.
/// </summary>
internal sealed record ApplicationFilterTarget(string Path, bool Directory, bool Name = false);
