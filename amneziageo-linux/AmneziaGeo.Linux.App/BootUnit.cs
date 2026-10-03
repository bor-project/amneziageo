namespace AmneziaGeo.Linux.App;

/// <summary>
/// Keeps the systemd unit the agent runs under started at boot exactly while survive-reboot is on.
/// </summary>
internal static class BootUnit
{
    private const string SystemSlice = "/system.slice/";

    /// <summary>
    /// The system unit named in a /proc/self/cgroup text, or null when the process is not in one.
    /// </summary>
    public static string? UnitOf(string cgroup)
    {
        foreach (var line in cgroup.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // hierarchy:controllers:path; a user session's terminal sits under user.slice, never here.
            var parts = line.Split(':', 3);
            if (parts.Length < 3 || !parts[2].StartsWith(SystemSlice, StringComparison.Ordinal))
            {
                continue;
            }

            var unit = parts[2][SystemSlice.Length..].Split('/')[0];
            if (unit.EndsWith(".service", StringComparison.Ordinal) && unit.Length > ".service".Length)
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>
    /// Enables or disables the agent's own unit at boot to match survive-reboot.
    /// </summary>
    public static async Task SyncAsync(bool atBoot, AgentLog log, CancellationToken ct)
    {
        if (Own() is not { } unit)
        {
            log.Debug("agent", "not run by a systemd unit: survive-reboot only decides whether the agent connects at start");
            return;
        }

        await SetAsync(unit, atBoot, log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enables or disables a unit at boot, leaving alone one that is static, masked or generated.
    /// </summary>
    public static async Task SetAsync(string unit, bool atBoot, AgentLog log, CancellationToken ct)
    {
        var state = (await Shell.RunAsync("systemctl", ct, "is-enabled", unit).ConfigureAwait(false)).Output.Trim();
        if (state is not ("enabled" or "disabled"))
        {
            log.Info("agent", $"{unit} is {(state.Length > 0 ? state : "unknown")} at boot; survive-reboot leaves it as it is");
            return;
        }

        if ((state == "enabled") == atBoot)
        {
            return;
        }

        var verb = atBoot ? "enable" : "disable";
        var (code, output) = await Shell.RunAsync("systemctl", ct, verb, unit).ConfigureAwait(false);
        if (code != 0)
        {
            log.Warn("agent", $"could not {verb} {unit} at boot: {output}");
            return;
        }

        log.Info("agent", atBoot
            ? $"{unit} starts at boot: survive-reboot is on"
            : $"{unit} no longer starts at boot: survive-reboot is off");
    }

    private static string? Own()
    {
        if (ContainerHost.Detected || Environment.GetEnvironmentVariable("INVOCATION_ID") is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return UnitOf(File.ReadAllText("/proc/self/cgroup"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
