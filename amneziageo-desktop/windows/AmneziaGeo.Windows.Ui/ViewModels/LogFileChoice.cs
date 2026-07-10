namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// One on-disk log file offered in the log viewer's file picker. Deserialized from the agent's OpListLogs
/// reply ({ name, type, size, modified }); Name is the identity used when reading the file back over IPC.
/// </summary>
internal sealed record LogFileChoice(string Name, string Type, long Size, string Modified)
{
    /// <summary>
    /// Combo-box label.
    /// </summary>
    public string Display => Name;
}
