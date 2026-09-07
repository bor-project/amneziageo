using System.Net.NetworkInformation;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Adapters of this machine, read while they are being raised and dropped.
/// </summary>
internal static class NetworkAdapters
{
    private const int Tries = 3;
    private const int WaitMs = 150;

    /// <summary>
    /// Every adapter of the machine; a refused enumeration is asked again, and an empty list ends it.
    /// </summary>
    public static NetworkInterface[] All()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException) when (attempt < Tries)
            {
                Thread.Sleep(WaitMs);
            }
            catch (NetworkInformationException)
            {
                return [];
            }
        }
    }
}
