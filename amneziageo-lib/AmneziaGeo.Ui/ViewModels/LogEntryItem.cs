namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One stored log row as a narrow window shows it: when it happened, how loud it was and what it says.
/// </summary>
internal sealed record LogEntryItem(string Time, string Level, string Text)
{
    /// <summary>
    /// Whether the row carries a level.
    /// </summary>
    public bool HasLevel => Level.Length > 0;

    /// <summary>
    /// Whether the row names a failure.
    /// </summary>
    public bool IsAlarm => Level is "ERR" or "FTL";

    /// <summary>
    /// Whether the row warns.
    /// </summary>
    public bool IsWarning => Level == "WRN";

    /// <summary>
    /// Whether the row carries a level that neither warns nor fails.
    /// </summary>
    public bool IsPlain => HasLevel && !IsAlarm && !IsWarning;

    /// <summary>
    /// Splits a rendered line into its parts: "yyyy-MM-dd HH:mm:ss.fff [LVL] rest" as the agents write it, and
    /// anything shaped otherwise goes in whole.
    /// </summary>
    public static LogEntryItem Parse(string line)
    {
        var stamped = line.Length > 24 && line[4] == '-' && line[10] == ' ' && line[23] == ' ';
        var time = stamped ? line[11..23] : string.Empty;
        var rest = stamped ? line[24..] : line;
        var level = string.Empty;
        if (rest.StartsWith('[') && rest.IndexOf(']') is var close and > 0)
        {
            level = rest[1..close];
            rest = rest[(close + 1)..].TrimStart();
        }

        return new LogEntryItem(time, level, rest);
    }
}
