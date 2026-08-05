using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// One line of the doctor report.
/// </summary>
/// <param name="Name">What was checked.</param>
/// <param name="Ok">Whether the check passed.</param>
/// <param name="Detail">What was found.</param>
public sealed record DoctorCheck(string Name, bool Ok, string Detail);

/// <summary>
/// What the console needs from the platform it runs on.
/// </summary>
public interface ICliHost
{
    /// <summary>
    /// Name the usage text calls the tool by.
    /// </summary>
    string ExeName { get; }

    /// <summary>
    /// Usage block for the commands this platform adds; empty when it adds none.
    /// </summary>
    string ExtraUsage { get; }

    /// <summary>
    /// Standard input, or null where the platform has none.
    /// </summary>
    TextReader? StandardInput { get; }

    /// <summary>
    /// Reaches the agent and waits for its first snapshot; null when it stayed silent.
    /// </summary>
    Task<IAgentLink?> ConnectAsync(TimeSpan commandTimeout, TimeSpan connectWait, CancellationToken ct);

    /// <summary>
    /// Why the agent could not be reached.
    /// </summary>
    string UnreachableHint();

    /// <summary>
    /// Runs a command that needs no agent; null when the command is not one of them.
    /// </summary>
    Task<int>? TryRunLocalAsync(IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>
    /// Runs a command this platform adds on top of the shared ones; null when the command is not one of them.
    /// </summary>
    Task<int>? TryRunWithAgentAsync(IAgentLink agent, IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>
    /// Platform checks the doctor report adds to the shared ones.
    /// </summary>
    IReadOnlyList<DoctorCheck> DoctorChecks(StatusSnapshot snapshot);
}
