using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// The setup one answer of the agent offers.
/// </summary>
/// <param name="Available">Whether the version is newer than the one installed.</param>
/// <param name="Version">The version the release carries.</param>
/// <param name="Url">The address of the setup.</param>
/// <param name="Sha256">The published digest of the setup, empty on a legacy manifest.</param>
/// <param name="Asset">The entry of the setup in the manifest of the release, null where the manifest lists none.</param>
internal sealed record UpdateOffer(bool Available, string Version, string Url, string Sha256, UpdateAsset? Asset)
{
    /// <summary>
    /// No offer.
    /// </summary>
    public static UpdateOffer None { get; } = new(false, string.Empty, string.Empty, string.Empty, null);

    /// <summary>
    /// Whether the offer names a setup to download.
    /// </summary>
    public bool Downloadable => Available && Url.Length > 0;

    /// <summary>
    /// The offer a snapshot carries.
    /// </summary>
    public static UpdateOffer Of(StatusSnapshot snapshot)
    {
        return new UpdateOffer(
            snapshot.UpdateAvailable,
            snapshot.UpdateVersion,
            snapshot.UpdateSetupUrl,
            snapshot.UpdateSetupSha256,
            snapshot.UpdateSetupAsset);
    }
}
