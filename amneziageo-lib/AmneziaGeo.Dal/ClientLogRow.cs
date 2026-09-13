using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AmneziaGeo.Dal;

/// <summary>
/// One row a client process hands to the agent log.
/// </summary>
public sealed record ClientLogRow(long UnixMs, int LevelId, string Source, string Message)
{
    // Levels a client process writes, from information to error.
    private const int LowestLevelId = 3;
    private const int HighestLevelId = 5;

    /// <summary>
    /// The arguments of the log-client request carrying the row.
    /// </summary>
    public IReadOnlyList<string> Args()
    {
        return [Message, LevelId.ToString(CultureInfo.InvariantCulture), Source, UnixMs.ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Reads a row back from log-client request arguments; a bare message is not one.
    /// </summary>
    public static bool TryRead(IReadOnlyList<string> args, [NotNullWhen(true)] out ClientLogRow? row)
    {
        row = IsRow(args, out var levelId, out var unixMs) ? new ClientLogRow(unixMs, levelId, args[2], args[0]) : null;
        return row is not null;
    }

    private static bool IsRow(IReadOnlyList<string> args, out int levelId, out long unixMs)
    {
        levelId = 0;
        unixMs = 0;
        return args.Count == 4
            && !string.IsNullOrWhiteSpace(args[0])
            && ClientLog.IsSource(args[2])
            && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out levelId)
            && levelId is >= LowestLevelId and <= HighestLevelId
            && long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out unixMs);
    }
}
