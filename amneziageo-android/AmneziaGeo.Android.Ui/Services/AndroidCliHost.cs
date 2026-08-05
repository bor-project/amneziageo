using System.Globalization;
using Android.App;
using Android.Net;
using AmneziaGeo.Android.Engine;
using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// The console on Android: the agent runs in this process, so there is nothing to dial.
/// </summary>
internal sealed class AndroidCliHost : ICliHost
{
    /// <inheritdoc/>
    public string ExeName => "amneziageo";

    /// <inheritdoc/>
    public string ExtraUsage => """
        this device
          Commands arrive as a broadcast and answer in the reply data:
            adb shell am broadcast -a org.amneziageo.android.CLI \
              -n org.amneziageo.android/.CliReceiver --es cmd "status"
          Add --es out <path> to also write the full text to a file.
          The whole answer is mirrored to logcat under the tag AmneziaGeoCli.
          'up' needs the VPN consent the system asks for once, in the app window.
        """;

    /// <inheritdoc/>
    public TextReader? StandardInput => null;

    /// <inheritdoc/>
    public async Task<IAgentLink?> ConnectAsync(TimeSpan commandTimeout, TimeSpan connectWait, CancellationToken ct)
    {
        var agent = AndroidAgentConnection.Current ?? new AndroidAgentConnection();
        agent.Start();

        var deadline = DateTime.UtcNow + connectWait;
        while (agent.Latest is null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return agent.Latest is null ? null : new AndroidAgentLink(agent);
    }

    /// <inheritdoc/>
    public string UnreachableHint() =>
        "the in-process agent did not produce a snapshot; check logcat under the tag AmneziaGeo";

    /// <inheritdoc/>
    public Task<int>? TryRunLocalAsync(IReadOnlyList<string> args, CancellationToken ct) => null;

    /// <inheritdoc/>
    public Task<int>? TryRunWithAgentAsync(IAgentLink agent, IReadOnlyList<string> args, CancellationToken ct) => null;

    /// <inheritdoc/>
    public IReadOnlyList<DoctorCheck> DoctorChecks(StatusSnapshot snapshot)
    {
        var context = Application.Context;
        var files = context.FilesDir?.AbsolutePath ?? string.Empty;
        var consented = VpnService.Prepare(context) is null;
        return
        [
            new("agent process", AndroidAgentConnection.Current is not null, global::Android.OS.Process.MyPid().ToString(CultureInfo.InvariantCulture)),
            new("library", files.Length > 0 && Directory.Exists(files), files.Length > 0 ? files : "no files directory"),
            new("vpn consent", consented, consented ? "granted" : "not granted: open the app once and connect"),
            new("tunnel process", true, VpnBridge.IsRunning(context) ? "running" : "not running"),
        ];
    }
}
