using System.Globalization;
using Android.App;
using Android.Net;
using AmneziaGeo.Android.Engine;
using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;

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

        // Waits for the loaded snapshot, not the first one: routing lists and transports arrive with the store.
        var ready = agent.ReadyAsync();
        var deadline = DateTime.UtcNow + connectWait;
        while (!ready.IsCompleted && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        if (agent.Latest is null)
        {
            return null;
        }

        agent.CatchUp();
        return new AndroidAgentLink(agent);
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
            .. AndroidBackgroundLimits.Read().Select(BackgroundCheck),
        ];
    }

    // Строка проверки по ограничению фона.
    private static DoctorCheck BackgroundCheck(BackgroundLimit limit)
    {
        var limited = limit.State == BackgroundLimitState.Limited;
        return limit.Kind switch
        {
            BackgroundLimitKind.Notifications => new("notifications", !limited, limited ? "off: the notice of the tunnel is not shown" : "on"),
            BackgroundLimitKind.Battery => new("battery saver", !limited, limit.State switch
            {
                BackgroundLimitState.Limited => "restricts the app in the background",
                BackgroundLimitState.Free => "does not restrict the app",
                _ => "Android has no exemption for the app, the setting of MIUI is not readable",
            }),
            _ => new("autostart", !limited, limit.State switch
            {
                BackgroundLimitState.Limited => "off: the system does not start the app by itself",
                BackgroundLimitState.Free => "on",
                _ => "the system does not tell",
            }),
        };
    }
}
