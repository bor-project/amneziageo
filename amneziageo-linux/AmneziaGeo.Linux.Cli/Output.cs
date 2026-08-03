using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Process exit codes.
/// </summary>
internal static class Exit
{
    /// <summary>
    /// The command succeeded.
    /// </summary>
    public const int Ok = 0;

    /// <summary>
    /// The agent refused the command.
    /// </summary>
    public const int Failed = 1;

    /// <summary>
    /// The command line is wrong.
    /// </summary>
    public const int Usage = 2;

    /// <summary>
    /// The agent could not be reached.
    /// </summary>
    public const int Unreachable = 3;

    /// <summary>
    /// The agent does not implement the operation on this platform.
    /// </summary>
    public const int Unsupported = 5;
}

/// <summary>
/// Console rendering: aligned tables, JSON, and messages.
/// </summary>
internal static class Output
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Whether commands print JSON instead of tables.
    /// </summary>
    public static bool Json { get; set; }

    /// <summary>
    /// Whether informational lines are suppressed.
    /// </summary>
    public static bool Quiet { get; set; }

    /// <summary>
    /// Writes a line to stdout.
    /// </summary>
    public static void Line(string text = "") => Console.Out.WriteLine(text);

    /// <summary>
    /// Writes a line unless output is quiet.
    /// </summary>
    public static void Info(string text)
    {
        if (!Quiet)
        {
            Console.Out.WriteLine(text);
        }
    }

    /// <summary>
    /// Writes a line to stderr.
    /// </summary>
    public static void Error(string text) => Console.Error.WriteLine(text);

    /// <summary>
    /// Serializes a value as JSON.
    /// </summary>
    public static void AsJson(object value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, _json));

    /// <summary>
    /// Prints an aligned table; an empty body prints nothing but the note.
    /// </summary>
    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, string? emptyNote = null)
    {
        if (rows.Count == 0)
        {
            if (emptyNote is { Length: > 0 })
            {
                Info(emptyNote);
            }

            return;
        }

        var widths = new int[headers.Count];
        for (var column = 0; column < headers.Count; column++)
        {
            widths[column] = headers[column].Length;
            foreach (var row in rows)
            {
                if (column < row.Count)
                {
                    widths[column] = Math.Max(widths[column], row[column].Length);
                }
            }
        }

        Line(Row(headers, widths));
        foreach (var row in rows)
        {
            Line(Row(row, widths));
        }
    }

    /// <summary>
    /// Prints a key/value block.
    /// </summary>
    public static void Pairs(IReadOnlyList<(string Key, string Value)> pairs)
    {
        var width = 0;
        foreach (var pair in pairs)
        {
            width = Math.Max(width, pair.Key.Length);
        }

        foreach (var pair in pairs)
        {
            Line($"{pair.Key.PadRight(width)}  {pair.Value}");
        }
    }

    private static string Row(IReadOnlyList<string> cells, int[] widths)
    {
        var builder = new StringBuilder();
        for (var column = 0; column < widths.Length; column++)
        {
            var cell = column < cells.Count ? cells[column] : string.Empty;
            builder.Append(column == widths.Length - 1 ? cell : cell.PadRight(widths[column] + 2));
        }

        return builder.ToString().TrimEnd();
    }
}
