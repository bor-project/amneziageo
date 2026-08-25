using AmneziaGeo.Ipc;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// What the system tethering carries right now.
/// </summary>
/// <param name="Supported">Whether this machine can raise an access point.</param>
/// <param name="Reason">What stands in the way; empty while nothing does.</param>
/// <param name="Running">Whether a point under the name asked about is up.</param>
/// <param name="Clients">Devices on it.</param>
/// <param name="MaxClients">How many it admits.</param>
/// <param name="Band">Band it stands on: 2.4, 5, or auto where the adapter chose; empty while it is down.</param>
internal sealed record TetheringState(
    bool Supported,
    string Reason,
    bool Running,
    int Clients,
    int MaxClients,
    string Band)
{
    /// <summary>
    /// Nothing standing, for the reason named.
    /// </summary>
    public static TetheringState Down(string reason) => new(false, reason, false, 0, 0, string.Empty);
}

/// <summary>
/// The system tethering behind an interface of plain values. Every WinRT type stays inside, so nothing of the
/// Windows projection is loaded on a machine that never raises an access point.
/// </summary>
internal static class WindowsTethering
{
    // The tethering rides on the Internet Connection Sharing service; stopped, it answers with this.
    private const int ServiceNotActive = unchecked((int)0x80070426);

    /// <summary>
    /// Reads what the machine can do and what stands, counting a point as up only under the name given.
    /// </summary>
    public static async Task<TetheringState> ReadAsync(string ownSsid)
    {
        if (await RadioOffAsync().ConfigureAwait(false))
        {
            return TetheringState.Down(HotspotReasons.RadioOff);
        }

        var (manager, reason) = Manager();
        if (manager is null)
        {
            return TetheringState.Down(reason);
        }

        var configuration = manager.GetCurrentAccessPointConfiguration();
        var running = manager.TetheringOperationalState == TetheringOperationalState.On
            && ownSsid.Length > 0
            && string.Equals(configuration.Ssid, ownSsid, StringComparison.Ordinal);
        return new TetheringState(
            true,
            HotspotReasons.Ready,
            running,
            running ? (int)manager.ClientCount : 0,
            (int)manager.MaxClientCount,
            running ? Token(configuration.Band) : string.Empty);
    }

    /// <summary>
    /// Raises the point on the name, password and band given, taking down what stands first. Answers with the
    /// fault, or empty when it came up.
    /// </summary>
    public static async Task<string> StartAsync(string ssid, string password, string band, CancellationToken ct)
    {
        var (manager, reason) = Manager();
        if (manager is null)
        {
            return reason;
        }

        // A configuration cannot be written under a running point, so a changed name or password takes it down
        // first.
        if (manager.TetheringOperationalState == TetheringOperationalState.On)
        {
            await manager.StopTetheringAsync().AsTask(ct).ConfigureAwait(false);
        }

        var configuration = manager.GetCurrentAccessPointConfiguration();
        configuration.Ssid = ssid;
        configuration.Passphrase = password;
        configuration.Band = Band(configuration, band);
        await manager.ConfigureAccessPointAsync(configuration).AsTask(ct).ConfigureAwait(false);

        var result = await manager.StartTetheringAsync().AsTask(ct).ConfigureAwait(false);
        return result.Status == TetheringOperationStatus.Success ? string.Empty : result.Status.ToString();
    }

    /// <summary>
    /// Takes down the point standing under the name given, and leaves one under any other name alone.
    /// </summary>
    public static async Task StopAsync(string ownSsid, CancellationToken ct)
    {
        var (manager, _) = Manager();
        if (manager is null || manager.TetheringOperationalState != TetheringOperationalState.On)
        {
            return;
        }

        if (ownSsid.Length > 0
            && string.Equals(manager.GetCurrentAccessPointConfiguration().Ssid, ownSsid, StringComparison.Ordinal))
        {
            await manager.StopTetheringAsync().AsTask(ct).ConfigureAwait(false);
        }
    }

    // The tethering of the connection this machine reaches the internet by, and the reason there is none.
    private static (NetworkOperatorTetheringManager? Manager, string Reason) Manager()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null)
        {
            return (null, HotspotReasons.NoApMode);
        }

        try
        {
            // The only answer worth trusting on whether this machine tethers: GetTetheringCapability reads a
            // mobile broadband account that a machine without one does not have, and throws.
            return (NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile), HotspotReasons.Ready);
        }
        catch (Exception ex) when (ex.HResult == ServiceNotActive)
        {
            return (null, HotspotReasons.ServiceOff);
        }
        catch (Exception)
        {
            return (null, HotspotReasons.NoApMode);
        }
    }

    // Read through the radio API rather than the text of netsh, which is translated with the system.
    private static async Task<bool> RadioOffAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
            var wifi = radios.FirstOrDefault(radio => radio.Kind == RadioKind.WiFi);
            return wifi is not null && wifi.State == RadioState.Off;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // A band the adapter refuses keeps the point from coming up, so an unsupported choice falls back to auto.
    private static TetheringWiFiBand Band(NetworkOperatorTetheringAccessPointConfiguration configuration, string band)
    {
        var wanted = band switch
        {
            HotspotBands.TwoPointFour => TetheringWiFiBand.TwoPointFourGigahertz,
            HotspotBands.Five => TetheringWiFiBand.FiveGigahertz,
            _ => TetheringWiFiBand.Auto,
        };

        try
        {
            return configuration.IsBandSupported(wanted) ? wanted : TetheringWiFiBand.Auto;
        }
        catch (Exception)
        {
            return TetheringWiFiBand.Auto;
        }
    }

    // Windows names the band asked for and never the channel taken, so auto stands for a band the adapter chose.
    private static string Token(TetheringWiFiBand band)
    {
        return band switch
        {
            TetheringWiFiBand.TwoPointFourGigahertz => HotspotBands.TwoPointFour,
            TetheringWiFiBand.FiveGigahertz => HotspotBands.Five,
            _ => HotspotBands.Auto,
        };
    }
}
