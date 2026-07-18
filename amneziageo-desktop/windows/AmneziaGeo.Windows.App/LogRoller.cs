namespace AmneziaGeo.Windows.App;

/// <summary>
/// Size-based rotation shared by the agent and routing logs: renames the live file to a numbered generation
/// and shifts older ones up, dropping the oldest past the retained count.
/// </summary>
internal static class LogRoller
{
    /// <summary>
    /// Roll threshold in bytes; the live file rotates once it grows past this.
    /// </summary>
    public const long MaxBytes = 8_000_000;

    /// <summary>
    /// Numbered generations kept alongside the live file (.1 = newest backup).
    /// </summary>
    public const int Retained = 5;

    /// <summary>
    /// Rotates path to path.1, shifting path.k to path.k+1 and dropping path.Retained.
    /// </summary>
    public static void Roll(string path)
    {
        var oldest = $"{path}.{Retained}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var k = Retained - 1; k >= 1; k--)
        {
            var from = $"{path}.{k}";
            if (File.Exists(from))
            {
                File.Move(from, $"{path}.{k + 1}");
            }
        }

        if (File.Exists(path))
        {
            File.Move(path, $"{path}.1");
        }
    }
}
