using System;
using System.Threading.Tasks;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// The dirty-tracking + commit contract shared by the autosave-capable editors (config transport / export,
/// routing list / settings, profile row). It tracks whether the surface holds uncommitted changes against a
/// captured baseline and commits them through the agent. Marking dirty compares the live fields to the baseline
/// and never persists on its own, so it cannot reintroduce the per-keystroke save that caused the #28/#33
/// focus-steal.
/// </summary>
internal interface IEditScope
{
    /// <summary>True while the surface holds an uncommitted change.</summary>
    bool IsDirty { get; }

    /// <summary>Raised whenever <see cref="IsDirty"/> may have changed.</summary>
    event EventHandler? DirtyChanged;

    /// <summary>
    /// Runs the pending edit's local (non-IPC) validation, surfacing its own status on failure. Scopes with no
    /// local validation return true.
    /// </summary>
    bool CanCommit();

    /// <summary>Snapshots the current field values as the new clean baseline.</summary>
    void CaptureBaseline();

    /// <summary>Restores the fields to the captured baseline, under the seed guard so it echoes no IPC.</summary>
    void Revert();

    /// <summary>Persists the pending change through the agent; returns whether it succeeded.</summary>
    Task<bool> CommitAsync();
}
