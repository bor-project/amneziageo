using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// The folder the Windows window downloads the setup into: the whole setup, or its update layout put together from
/// the installed copy and the files of the release pack the copy lacks.
/// </summary>
internal static class SetupDownloads
{
    /// <summary>
    /// The bundle an update layout runs.
    /// </summary>
    public const string LayoutSetup = "AmneziaGeoSetup.exe";

    // A digest no file has: the setup it stands for is not the one on offer.
    private const string NoDigest = "-";

    /// <summary>
    /// The folder of the downloads.
    /// </summary>
    public static string Folder { get; } = Path.Combine(Path.GetTempPath(), "AmneziaGeoUpdate");

    /// <summary>
    /// Where the whole setup lands.
    /// </summary>
    public static string WholePath(string folder, string setup) => Path.Combine(folder, setup);

    /// <summary>
    /// Puts the update layout of the setup together in the folder and returns the bundle to run: the files the
    /// installed copy under the folders held has are taken from it, the rest come from the pack.
    /// </summary>
    public static async Task<(string Setup, UpdatePieceCount Count)> StageAsync(
        HttpClient http,
        Uri address,
        UpdateAsset asset,
        IReadOnlyList<string> held,
        string folder,
        Action<long, long>? progress,
        CancellationToken ct)
    {
        var layout = LayoutPath(folder, Path.GetFileName(address.LocalPath));
        var count = await new UpdatePieces(http).StageAsync(address, asset, held, layout, progress, ct).ConfigureAwait(false);
        var setup = Path.Combine(layout, LayoutSetup);
        if (!File.Exists(setup))
        {
            throw new InvalidDataException($"the update layout carries no {LayoutSetup}");
        }

        return (setup, count);
    }

    /// <summary>
    /// Drops the downloads of every setup but the one named.
    /// </summary>
    public static void Sweep(string folder, string setup)
    {
        foreach (var entry in Entries(folder).Where(entry => !Belongs(entry, setup)))
        {
            Delete(entry);
        }
    }

    /// <summary>
    /// Drops the downloads of the setup named.
    /// </summary>
    public static void Drop(string folder, string setup)
    {
        foreach (var entry in Entries(folder).Where(entry => Belongs(entry, setup)))
        {
            Delete(entry);
        }
    }

    /// <summary>
    /// Drops the downloads of the version running and of earlier ones.
    /// </summary>
    public static void DropInstalled(string folder, Version running)
    {
        foreach (var entry in Entries(folder))
        {
            var parts = Path.GetFileName(entry).Split('-');
            if (parts.Length > 1 && parts[0] == "AmneziaGeo" && Version.TryParse(parts[1], out var version) && version <= running)
            {
                Delete(entry);
            }
        }
    }

    /// <summary>
    /// Drops the update layout of the setup named and its list.
    /// </summary>
    public static void DropLayout(string folder, string setup)
    {
        var layout = LayoutPath(folder, setup);
        foreach (var entry in new[] { layout, layout + ".old", layout + ".files" })
        {
            Delete(entry);
        }
    }

    /// <summary>
    /// The digest a downloaded setup has to have: the one the list of its layout names while the manifest vouches
    /// for that list, the published digest of the whole setup otherwise.
    /// </summary>
    public static string DigestFor(string path, UpdateAsset? asset, string published)
    {
        var list = Path.GetDirectoryName(path) + ".files";
        if (!string.Equals(Path.GetFileName(path), LayoutSetup, StringComparison.OrdinalIgnoreCase) || !File.Exists(list))
        {
            return published;
        }

        if (asset?.Files?.Sha256 is not { Length: > 0 } digest || asset.Pack is not { Size: > 0 } pack)
        {
            return NoDigest;
        }

        try
        {
            var text = File.ReadAllBytes(list);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(text)), digest, StringComparison.OrdinalIgnoreCase))
            {
                return NoDigest;
            }

            return UpdateList.Parse(text, pack.Size)
                .FirstOrDefault(entry => entry.Kind == UpdateEntryKind.File && entry.Path == LayoutSetup)?
                .Sha256 ?? NoDigest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return NoDigest;
        }
    }

    private static string LayoutPath(string folder, string setup) =>
        Path.Combine(folder, Path.GetFileNameWithoutExtension(setup));

    // Whether a download belongs to the setup: its layout, its list, the setup itself or its partial.
    private static bool Belongs(string entry, string setup)
    {
        var name = Path.GetFileName(entry);
        var stem = Path.GetFileNameWithoutExtension(setup);
        return string.Equals(name, stem, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Entries(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).ToList() : [];

    private static void Delete(string entry)
    {
        try
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
