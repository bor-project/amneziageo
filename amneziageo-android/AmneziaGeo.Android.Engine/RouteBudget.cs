using Android.OS;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// How many routes a tunnel built without the relay carries.
/// </summary>
public static class RouteBudget
{
    // establish() hands the whole route table over in one Binder transaction: 80 bytes a route against a 1 MB parcel.
    private const int BytesPerRoute = 80;

    private const int ParcelBytes = 1024 * 1024;

    /// <summary>
    /// Routes such a tunnel takes, leaving room for the addresses a session adds at connect.
    /// </summary>
    public const int Max = ParcelBytes / BytesPerRoute * 9 / 10;

    /// <summary>
    /// Whether this device builds the tunnel without the relay, so the ceiling applies.
    /// </summary>
    public static bool Applies => Build.VERSION.SdkInt < BuildVersionCodes.Q;
}
