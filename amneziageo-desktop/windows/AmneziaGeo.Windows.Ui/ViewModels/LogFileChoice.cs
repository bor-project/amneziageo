using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// One on-disk log file offered in the log viewer's file picker. Deserialized from the agent's OpListLogs
/// reply ({ name, type, size, modified }); Name is the identity used when reading the file back over IPC.
/// </summary>
internal sealed record LogFileChoice(string Name, string Type, long Size, string Modified)
{
    /// <summary>
    /// Localized coarse type of the file (agent log vs routing log).
    /// </summary>
    public string TypeLabel => Type switch
    {
        "agent" => Loc.Instance.Get("MainVm_LogTypeAgent"),
        "routes" => Loc.Instance.Get("MainVm_LogTypeRoutes"),
        _ => Loc.Instance.Get("MainVm_LogTypeOther"),
    };

    /// <summary>
    /// Combo-box label: the file name tagged with its localized type.
    /// </summary>
    public string Display => $"{Name}  ·  {TypeLabel}";
}
