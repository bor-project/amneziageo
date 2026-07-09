using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Aggregates the <see cref="IEditScope"/>s of the item currently open in the settings pane (#143). Exposes a
/// single <see cref="IsEditing"/> flag (any scope dirty) plus Save / Cancel that commit or revert every dirty
/// scope. The host (MainWindowViewModel) re-points the active scopes via <see cref="SetScopes"/> whenever the
/// open item or section changes, and blocks navigation while editing - so the active set never swaps mid-edit.
/// </summary>
internal sealed class EditController
{
    private readonly List<IEditScope> _scopes = [];

    /// <summary>Raised when <see cref="IsEditing"/> may have changed (scope set replaced, or a scope flipped).</summary>
    public event EventHandler? EditingChanged;

    /// <summary>True when any active scope holds an uncommitted change.</summary>
    public bool IsEditing => _scopes.Any(scope => scope.IsDirty);

    /// <summary>
    /// Replaces the active scope set (nulls ignored, order preserved for commit sequencing). Unsubscribes the
    /// previous scopes and subscribes the new ones.
    /// </summary>
    public void SetScopes(params IEditScope?[] scopes)
    {
        foreach (var scope in _scopes)
        {
            scope.DirtyChanged -= OnScopeDirtyChanged;
        }

        _scopes.Clear();
        foreach (var scope in scopes)
        {
            if (scope is not null)
            {
                _scopes.Add(scope);
                scope.DirtyChanged += OnScopeDirtyChanged;
            }
        }

        RaiseEditingChanged();
    }

    private void OnScopeDirtyChanged(object? sender, EventArgs e) => RaiseEditingChanged();

    private void RaiseEditingChanged() => EditingChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Commits every dirty scope in order and re-captures each as its new clean baseline on success. Stops at
    /// the first failure, leaving that scope (and any after it) dirty; the failing scope surfaces its own
    /// status. Returns whether all commits succeeded. Iterates a snapshot so a commit that re-points the scope
    /// set (a new list's first save builds its settings editor) does not mutate the collection mid-loop.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        foreach (var scope in _scopes.ToArray())
        {
            if (!scope.IsDirty)
            {
                continue;
            }

            if (!await scope.CommitAsync())
            {
                RaiseEditingChanged();
                return false;
            }

            scope.CaptureBaseline();
        }

        RaiseEditingChanged();
        return true;
    }

    /// <summary>Reverts every dirty scope to its captured baseline.</summary>
    public void Cancel()
    {
        foreach (var scope in _scopes.ToArray())
        {
            if (scope.IsDirty)
            {
                scope.Revert();
            }
        }

        RaiseEditingChanged();
    }
}
