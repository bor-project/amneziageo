namespace AmneziaGeo.Windows.App;

/// <summary>
/// Appends timestamped lines to a log file, used where there is no console (the agent service).
/// </summary>
internal sealed class FileLogger
{
    private readonly string _path;
    private readonly Lock _gate = new();

    /// <summary>
    /// ctor
    /// </summary>
    public FileLogger(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>
    /// Appends a single timestamped line.
    /// </summary>
    public void Log(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:u} {message}{Environment.NewLine}";
        lock (_gate)
        {
            File.AppendAllText(_path, line);
        }
    }
}
