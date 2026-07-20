using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// A fatal connect failure carrying its structured reason.
/// </summary>
internal sealed class ConnectFailureException(ConnectFailureReason reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>
    /// The classified cause of the failure.
    /// </summary>
    public ConnectFailureReason Reason { get; } = reason;
}
