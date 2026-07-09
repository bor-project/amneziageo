using System;
using System.Threading.Tasks;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// An editable surface that participates in the atomic per-item edit model (#143). It tracks whether it holds
/// uncommitted changes against a captured baseline, can revert to that baseline, and commits through the agent.
/// The host (<see cref="EditController"/>) aggregates the currently-open item's scopes into a single
/// IsEditing / Save / Cancel and blocks navigation while any scope is dirty. Marking dirty compares the live
/// fields to the baseline and never persists on its own, so it cannot reintroduce the per-keystroke save that
/// caused the #28/#33 focus-steal.
/// </summary>
internal interface IEditScope
{
    /// <summary>True while the surface holds an uncommitted change.</summary>
    bool IsDirty { get; }

    /// <summary>Raised whenever <see cref="IsDirty"/> may have changed.</summary>
    event EventHandler? DirtyChanged;

    /// <summary>
    /// Runs the pending edit's local (non-IPC) validation, surfacing its own status on failure. The controller
    /// calls this for every dirty scope BEFORE persisting any, so a local rejection aborts the whole Save
    /// without leaving a multi-scope item half-committed. Scopes with no local validation return true.
    /// </summary>
    bool CanCommit();

    /// <summary>Snapshots the current field values as the new clean baseline.</summary>
    void CaptureBaseline();

    /// <summary>Restores the fields to the captured baseline, under the seed guard so it echoes no IPC.</summary>
    void Revert();

    /// <summary>Persists the pending change through the agent; returns whether it succeeded.</summary>
    Task<bool> CommitAsync();
}

/// <summary>
/// An <see cref="IEditScope"/> backed by delegates, for edit state that lives directly on the host view model
/// (e.g. the inline profile / config rename fields) rather than in a dedicated sub-view-model. The host calls
/// <see cref="RaiseDirtyChanged"/> from its OnXChanged handler after recomputing dirtiness.
/// </summary>
internal sealed class DelegateEditScope : IEditScope
{
    private readonly Func<bool> _isDirty;
    private readonly Action _capture;
    private readonly Action _revert;
    private readonly Func<Task<bool>> _commit;
    private readonly Func<bool>? _canCommit;

    public DelegateEditScope(Func<bool> isDirty, Action capture, Action revert, Func<Task<bool>> commit, Func<bool>? canCommit = null)
    {
        _isDirty = isDirty;
        _capture = capture;
        _revert = revert;
        _commit = commit;
        _canCommit = canCommit;
    }

    public bool IsDirty => _isDirty();

    public event EventHandler? DirtyChanged;

    /// <summary>The host raises this after its OnXChanged handler updates the underlying value.</summary>
    public void RaiseDirtyChanged() => DirtyChanged?.Invoke(this, EventArgs.Empty);

    public bool CanCommit() => _canCommit?.Invoke() ?? true;

    public void CaptureBaseline() => _capture();

    public void Revert() => _revert();

    public Task<bool> CommitAsync() => _commit();
}
