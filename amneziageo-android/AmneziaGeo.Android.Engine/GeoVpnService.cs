using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Hosts the AmneziaWG tunnel over Android VpnService.
/// </summary>
[Service(Permission = "android.permission.BIND_VPN_SERVICE", Exported = false)]
[IntentFilter(new[] { "android.net.VpnService" })]
public sealed class GeoVpnService : VpnService
{
    /// <inheritdoc/>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        return StartCommandResult.Sticky;
    }

    /// <inheritdoc/>
    public override void OnDestroy()
    {
        base.OnDestroy();
    }
}