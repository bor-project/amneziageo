using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace AmneziaGeo.Decl;

/// <summary>
/// The channels the releases of the client come in.
/// </summary>
public static class UpdateChannels
{
    /// <summary>
    /// Stable releases only.
    /// </summary>
    public const string Stable = "stable";

    /// <summary>
    /// Stable releases and betas.
    /// </summary>
    public const string Beta = "beta";

    /// <summary>
    /// Every release, the test builds too.
    /// </summary>
    public const string Test = "test";

    /// <summary>
    /// The channels in the order they widen.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Stable, Beta, Test];

    /// <summary>
    /// The channel a word names; the fallback for any other word.
    /// </summary>
    public static string Of(string? word, string fallback) => word?.Trim().ToLowerInvariant() switch
    {
        Stable => Stable,
        Beta => Beta,
        Test => Test,
        _ => fallback,
    };

    /// <summary>
    /// The channel the switch of the prereleases stands for.
    /// </summary>
    public static string Of(bool prerelease) => prerelease ? Beta : Stable;

    /// <summary>
    /// Whether a channel takes prereleases.
    /// </summary>
    public static bool Prerelease(string channel) => channel is Beta or Test;
}

/// <summary>
/// A server that hands out the releases of the client, as the server of a config names it.
/// </summary>
/// <param name="Url">The address the manifests of the channels lie under.</param>
/// <param name="Pin">The SHA-256 of the certificate the address answers under, empty when it names none.</param>
/// <param name="Default">Whether the server asks to be taken for the updates while the device has picked none.</param>
/// <param name="Config">The config whose server names it.</param>
public sealed record UpdateServer(string Url, string Pin, bool Default, string Config);

/// <summary>
/// Where the installed copy of the client came from.
/// </summary>
/// <param name="Url">The address of the server it came from; empty for the address baked into the build.</param>
/// <param name="Pin">The SHA-256 of the certificate the server answered under.</param>
/// <param name="Version">The version it brought.</param>
public sealed record UpdateOrigin(string Url, string Pin, string Version)
{
    /// <summary>
    /// The address baked into the build.
    /// </summary>
    public static UpdateOrigin Baked { get; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// Whether it is a server of ours rather than the releases of the project.
    /// </summary>
    public bool Server => Url.Length > 0;

    /// <summary>
    /// Writes it for the store.
    /// </summary>
    public string Write() => JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["url"] = Url,
        ["pin"] = Pin,
        ["version"] = Version,
    });

    /// <summary>
    /// Reads it back from the store; null when nothing readable is kept.
    /// </summary>
    public static UpdateOrigin? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var url = Text(root, "url");

            return new UpdateOrigin(url.Length == 0 ? string.Empty : UpdateSources.Base(url) ?? string.Empty, Text(root, "pin"), Text(root, "version"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// One source the list of the update sources shows.
/// </summary>
/// <param name="Key">What picks it: origin for the source the installed copy came from, the address of a server otherwise.</param>
/// <param name="Host">The host it answers at, GitHub for the releases of the project.</param>
/// <param name="Config">The config whose server names it, empty for the releases of the project.</param>
/// <param name="Recommended">Whether its server asks to be taken for the updates.</param>
public sealed record UpdateSourceEntry(string Key, string Host, string Config, bool Recommended);

/// <summary>
/// The source a check reads.
/// </summary>
/// <param name="Key">The entry of the list it stands for.</param>
/// <param name="Url">The address of the server, or the manifest baked into the build.</param>
/// <param name="Pin">The SHA-256 of the certificate the server answers under, empty where the machine proves it.</param>
/// <param name="Server">Whether it is a server of ours rather than the releases of the project.</param>
/// <param name="Origin">Whether the installed copy came from it, so the patches of its releases fit.</param>
/// <param name="Gone">The host of the server picked by hand that hands out no updates any more, empty while it does.</param>
public sealed record UpdateRoute(string Key, string Url, string Pin, bool Server, bool Origin, string Gone)
{
    /// <summary>
    /// The host it answers at.
    /// </summary>
    public string Host => UpdateSources.HostOf(Url);

    /// <summary>
    /// The address of the manifest of a channel.
    /// </summary>
    public string Manifest(string channel) => Server ? Url + channel + "/update.json" : Url;

    /// <summary>
    /// What the device records as its origin when it hands the setup of a version from here to the installer.
    /// </summary>
    public UpdateOrigin Handed(string version) => Server ? new UpdateOrigin(Url, Pin, version) : UpdateOrigin.Baked with { Version = version };
}

/// <summary>
/// The release a check found and the address its files lie under.
/// </summary>
/// <param name="Manifest">The manifest of the release.</param>
/// <param name="BaseUrl">The address of the manifest; the files lie beside it.</param>
public sealed record UpdateRelease(UpdateManifest Manifest, Uri BaseUrl);

/// <summary>
/// The failure of the last search for updates at a server, and when it began.
/// </summary>
/// <param name="Text">What went wrong, empty while the search goes through.</param>
/// <param name="Since">When it began, in milliseconds of the Unix time; 0 while nothing is wrong.</param>
public sealed record UpdateFault(string Text, long Since)
{
    /// <summary>
    /// Nothing wrong.
    /// </summary>
    public static UpdateFault None { get; } = new(string.Empty, 0);
}

/// <summary>
/// Where the device takes the releases of the client from: the source its installed copy came from, or one of the
/// servers of its configs.
/// </summary>
public static class UpdateSources
{
    /// <summary>
    /// The key of the source the installed copy came from.
    /// </summary>
    public const string OriginKey = "origin";

    /// <summary>
    /// The word that leaves the pick to the servers.
    /// </summary>
    public const string AutoWord = "auto";

    /// <summary>
    /// The setting the origin is kept under.
    /// </summary>
    public const string OriginSetting = "update-origin";

    /// <summary>
    /// The setting the source handed the last setup to the installer is kept under.
    /// </summary>
    public const string HandedSetting = "update-origin-next";

    /// <summary>
    /// The text of a server that does not answer.
    /// </summary>
    public const string UnreachableKey = "Agent_UpdateSourceUnreachable";

    /// <summary>
    /// The text of a server that holds no release of the channel.
    /// </summary>
    public const string NoReleaseKey = "Agent_UpdateSourceNoRelease";

    /// <summary>
    /// The text of a server whose certificate is not the one its config names.
    /// </summary>
    public const string CertificateKey = "Agent_UpdateSourceCertificate";

    /// <summary>
    /// The text of a server picked by hand that hands out no updates any more.
    /// </summary>
    public const string GoneKey = "Agent_UpdateSourceGone";

    private const string ProjectHost = "GitHub";
    private const string UserAgent = "AmneziaGeo-UpdateChecker";

    private static readonly ConcurrentDictionary<string, HttpClient> _pinned = new(StringComparer.Ordinal);

    /// <summary>
    /// The address the releases of a server lie under, ending in a slash; null when the text is no such address.
    /// </summary>
    public static string? Base(string? text)
    {
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
            || address.Query.Length > 0
            || address.Fragment.Length > 0
            || address.UserInfo.Length > 0)
        {
            return null;
        }

        var url = address.GetLeftPart(UriPartial.Path);

        return url.EndsWith('/') ? url : url + "/";
    }

    /// <summary>
    /// The pick a word names: empty for none, origin, or the address of a server; null when the word names none of
    /// them.
    /// </summary>
    public static string? Choice(string? word)
    {
        var text = word?.Trim() ?? string.Empty;
        if (text.Length == 0 || string.Equals(text, AutoWord, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return string.Equals(text, OriginKey, StringComparison.OrdinalIgnoreCase) ? OriginKey : Base(text);
    }

    /// <summary>
    /// The servers that hand out updates, one per address, in the order of the configs that name them.
    /// </summary>
    public static IReadOnlyList<UpdateServer> Servers(IEnumerable<(string Config, ServerOffer Offer)> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);

        var servers = new List<UpdateServer>();
        foreach (var (config, offer) in offers)
        {
            if (offer.Updates() is not { } updates)
            {
                continue;
            }

            var index = servers.FindIndex(server => Same(server.Url, updates.Url));
            if (index < 0)
            {
                servers.Add(new UpdateServer(updates.Url, updates.Pin, updates.Default, config));
            }
            else if (updates.Default && !servers[index].Default)
            {
                servers[index] = servers[index] with { Default = true };
            }
        }

        return servers;
    }

    /// <summary>
    /// The origin once the version running is known: the source that handed a setup to the installer becomes the
    /// origin when the version it brought, or a later one, runs.
    /// </summary>
    public static (UpdateOrigin Origin, UpdateOrigin? Handed) Settle(UpdateOrigin origin, UpdateOrigin? handed, string running)
    {
        ArgumentNullException.ThrowIfNull(origin);

        if (handed is null)
        {
            return (origin, null);
        }

        return Version.TryParse(handed.Version, out var brought)
            && Version.TryParse(running, out var held)
            && held.CompareTo(brought) >= 0
                ? (handed, null)
                : (origin, handed);
    }

    /// <summary>
    /// The source a check reads: the server picked by hand, else the first server that asks to be taken, else the
    /// origin. A server picked by hand that hands out no updates any more gives way to the origin.
    /// </summary>
    public static UpdateRoute Pick(string baked, UpdateOrigin origin, string choice, IReadOnlyList<UpdateServer> servers)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(servers);

        var home = Home(baked, origin, servers);
        if (choice.Length == 0)
        {
            var offered = servers.FirstOrDefault(server => server.Default);

            return offered is null || (home.Server && Same(offered.Url, home.Url)) ? home : Route(offered, origin);
        }

        if (choice == OriginKey)
        {
            return home;
        }

        var picked = servers.FirstOrDefault(server => Same(server.Url, choice));
        if (picked is null)
        {
            return home with { Gone = HostOf(choice) };
        }

        return home.Server && Same(picked.Url, home.Url) ? home : Route(picked, origin);
    }

    /// <summary>
    /// The sources the list shows: the origin first, then every other server.
    /// </summary>
    public static IReadOnlyList<UpdateSourceEntry> Entries(string baked, UpdateOrigin origin, IReadOnlyList<UpdateServer> servers)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(servers);

        var home = origin.Server ? servers.FirstOrDefault(server => Same(server.Url, origin.Url)) : null;
        var entries = new List<UpdateSourceEntry>
        {
            home is null
                ? new UpdateSourceEntry(OriginKey, HostOf(baked), string.Empty, false)
                : new UpdateSourceEntry(OriginKey, HostOf(home.Url), home.Config, home.Default),
        };

        entries.AddRange(servers
            .Where(server => !ReferenceEquals(server, home))
            .Select(server => new UpdateSourceEntry(server.Url, HostOf(server.Url), server.Config, server.Default)));

        return entries;
    }

    /// <summary>
    /// The host an address answers at, GitHub for the releases of the project.
    /// </summary>
    public static string HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            return url;
        }

        if (string.Equals(address.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || address.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectHost;
        }

        return address.IsDefaultPort ? address.Host : address.Authority;
    }

    /// <summary>
    /// The client that reads a server under the certificate pinned besides the ones the machine proves; null where
    /// nothing is pinned.
    /// </summary>
    public static HttpClient? Pinned(string pin)
    {
        if (string.IsNullOrEmpty(pin))
        {
            return null;
        }

        return _pinned.GetOrAdd(pin, key =>
        {
            var handler = new SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None
                || (certificate is not null
                    && string.Equals(Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData())), key, StringComparison.OrdinalIgnoreCase));

            return new HttpClient(handler);
        });
    }

    /// <summary>
    /// Reads the release a source offers on a channel: the manifest of the channel at a server; at the releases of the
    /// project the newest release with the prereleases on a channel that takes them, the baked manifest otherwise.
    /// </summary>
    /// <param name="http">The client of the releases of the project.</param>
    /// <param name="route">The source.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="reading">Told every address read, for the row of a failure.</param>
    /// <param name="ct">Cancels the reading.</param>
    public static async Task<UpdateRelease?> FetchAsync(HttpClient http, UpdateRoute route, string channel, Action<string> reading, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(reading);

        if (route.Server)
        {
            var address = route.Manifest(channel);
            reading(address);
            var json = await (Pinned(route.Pin) ?? http).GetStringAsync(address, ct).ConfigureAwait(false);

            return UpdateFeed.ParseManifest(json) is { } manifest ? new UpdateRelease(manifest, new Uri(address)) : null;
        }

        if (UpdateChannels.Prerelease(channel) && UpdateFeed.TryGitHubRepo(route.Url, out var owner, out var repo))
        {
            var releases = UpdateFeed.ReleasesUrl(owner, repo);
            reading(releases);
            using var request = new HttpRequestMessage(HttpMethod.Get, releases);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode
                && UpdateFeed.SelectManifestUrl(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is { } picked)
            {
                reading(picked);
                if (UpdateFeed.ParseManifest(await http.GetStringAsync(picked, ct).ConfigureAwait(false)) is { } prerelease)
                {
                    return new UpdateRelease(prerelease, new Uri(picked));
                }
            }
        }

        reading(route.Url);
        var stable = await http.GetStringAsync(route.Url, ct).ConfigureAwait(false);

        return UpdateFeed.ParseManifest(stable) is { } found ? new UpdateRelease(found, new Uri(route.Url)) : null;
    }

    /// <summary>
    /// The published file of a release as the device may take it from a source: the patch beside it only from the
    /// source the installed copy came from.
    /// </summary>
    public static UpdateAsset? Fitting(UpdateAsset? asset, UpdateRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        return asset is { Patch: not null } && !route.Origin ? asset with { Patch = null } : asset;
    }

    /// <summary>
    /// The text key of what kept a check of a source from a release: the server picked by hand gone, the failure of
    /// the reading at a server, nothing for the releases of the project or a check that went through.
    /// </summary>
    /// <param name="route">The source checked.</param>
    /// <param name="found">Whether a release was read.</param>
    /// <param name="error">What the reading threw, null when it threw nothing.</param>
    public static string FaultKey(UpdateRoute route, bool found, Exception? error)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (route.Gone.Length > 0)
        {
            return GoneKey;
        }

        if (!route.Server || (found && error is null))
        {
            return string.Empty;
        }

        return error switch
        {
            null or JsonException => NoReleaseKey,
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => NoReleaseKey,
            HttpRequestException { InnerException: AuthenticationException } => CertificateKey,
            _ => UnreachableKey,
        };
    }

    /// <summary>
    /// The fault after a check: the one held while the text stays the same, a new one from now when it changes.
    /// </summary>
    public static UpdateFault Next(UpdateFault held, string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(held);

        if (string.Equals(held.Text, text, StringComparison.Ordinal))
        {
            return held;
        }

        return text.Length == 0 ? UpdateFault.None : new UpdateFault(text, now.ToUnixTimeMilliseconds());
    }

    // Addresses of the same server; the host goes in any case.
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // The source the installed copy came from: its server while a config still names it, the baked address otherwise.
    private static UpdateRoute Home(string baked, UpdateOrigin origin, IReadOnlyList<UpdateServer> servers)
    {
        if (origin.Server && servers.FirstOrDefault(server => Same(server.Url, origin.Url)) is { } server)
        {
            return new UpdateRoute(OriginKey, server.Url, server.Pin, true, true, string.Empty);
        }

        return new UpdateRoute(OriginKey, baked, string.Empty, false, !origin.Server, string.Empty);
    }

    private static UpdateRoute Route(UpdateServer server, UpdateOrigin origin) =>
        new(server.Url, server.Url, server.Pin, true, origin.Server && Same(origin.Url, server.Url), string.Empty);
}
