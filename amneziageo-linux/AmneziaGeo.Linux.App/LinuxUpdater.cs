using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Application update for the Debian packages: reads the release manifest, puts the packages this machine carries
/// together from their installed files and the patch of the release or downloads them whole, and hands them to apt in
/// a transient unit that outlives the agent restart the install triggers.
/// </summary>
internal sealed class LinuxUpdater : IDisposable
{
    private const string CorePackage = "amneziageo";
    private const string GuiPackage = "amneziageo-gui";
    private const string TransientUnit = "amneziageo-update";
    private const string Platform = "linux";

    private static readonly string[] _packages = [CorePackage, GuiPackage];

    private readonly HttpClient _http;
    private readonly AgentLog _log;
    private string _reading = string.Empty;
    private readonly Func<CancellationToken, Task> _push;
    private readonly IStateStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<UpdateServer>>> _servers;

    // The client the packages of the last release found are read with, under the pin of its source.
    private HttpClient _files;
    private UpdateRoute? _route;
    private UpdateOrigin? _handed;

    // The source and the channel the release on offer came from.
    private (string Url, string Channel) _offeredBy = (string.Empty, string.Empty);
    private readonly string _directory = Path.Combine(AgentPaths.Root, "updates");
    private readonly Lock _gate = new();

    // Digest of every package this agent built from a patch, by its path.
    private readonly ConcurrentDictionary<string, string> _built = new(StringComparer.Ordinal);

    private IReadOnlyList<PendingAsset> _assets = [];
    private CancellationTokenSource? _download;
    private string _downloadedVersion = string.Empty;
    private bool _disposed;
    private bool _applying;

    /// <summary>
    /// ctor
    /// </summary>
    public LinuxUpdater(HttpClient http, AgentLog log, Func<CancellationToken, Task> push, IStateStore store, Func<CancellationToken, Task<IReadOnlyList<UpdateServer>>> servers)
    {
        _http = http;
        _files = http;
        _log = log;
        _push = push;
        _store = store;
        _servers = servers;
    }

    /// <summary>
    /// Update metadata URL baked into this build; empty hides the update section.
    /// </summary>
    public string Url => AgentBuild.UpdateUrl;

    /// <summary>
    /// The channel the releases come in.
    /// </summary>
    public string Channel { get; private set; } = AgentBuild.Channel;

    /// <summary>
    /// The source picked by hand: origin or the address of a server; empty while the servers decide.
    /// </summary>
    public string Choice { get; private set; } = string.Empty;

    /// <summary>
    /// Where the installed packages came from.
    /// </summary>
    public UpdateOrigin Origin { get; private set; } = UpdateOrigin.Baked;

    /// <summary>
    /// What kept the last check of a server from a release.
    /// </summary>
    public UpdateFault Fault { get; private set; } = UpdateFault.None;

    /// <summary>
    /// Whether the offered version differs from the installed one.
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>
    /// Version the release offers.
    /// </summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>
    /// Release description.
    /// </summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Download URL of the agent package.
    /// </summary>
    public string SetupUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Published SHA-256 of the agent package.
    /// </summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>
    /// Path of the downloaded agent package.
    /// </summary>
    public string SetupPath { get; private set; } = string.Empty;

    /// <summary>
    /// Whether a manual check is running.
    /// </summary>
    public bool Checking { get; private set; }

    /// <summary>
    /// Whether the last manual check failed.
    /// </summary>
    public bool CheckFailed { get; private set; }

    /// <summary>
    /// Whether the packages are downloading.
    /// </summary>
    public bool Downloading { get; private set; }

    /// <summary>
    /// Whether every package is downloaded and verified.
    /// </summary>
    public bool Downloaded { get; private set; }

    /// <summary>
    /// Download progress in percent.
    /// </summary>
    public int Percent { get; private set; }

    /// <summary>
    /// Whether the last download or install failed.
    /// </summary>
    public bool Failed { get; private set; }

    /// <summary>
    /// Whether a running download has been asked to stop.
    /// </summary>
    public bool CancelRequested { get; private set; }

    /// <summary>
    /// Whether apt is installing the downloaded packages.
    /// </summary>
    public bool Installing { get; private set; }

    /// <summary>
    /// Takes the channel, the source and the origin from the stored settings; the source that handed the packages
    /// to apt becomes the origin once their version runs.
    /// </summary>
    public async Task LoadAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        Channel = UpdateChannels.Of(settings.GetValueOrDefault(SettingKeys.UpdateChannel), Channel);
        Choice = UpdateSources.Choice(settings.GetValueOrDefault(SettingKeys.UpdateSource)) ?? string.Empty;
        var kept = UpdateOrigin.Read(settings.GetValueOrDefault(UpdateSources.OriginSetting)) ?? UpdateOrigin.Baked;
        var handed = UpdateOrigin.Read(settings.GetValueOrDefault(UpdateSources.HandedSetting));
        (Origin, _handed) = UpdateSources.Settle(kept, handed, AgentBuild.Version);
        if (!ReferenceEquals(_handed, handed))
        {
            await KeepOriginAsync(ct).ConfigureAwait(false);
            _log.Info("update", $"the installed packages came from {(Origin.Server ? UpdateSources.HostOf(Origin.Url) : "the releases of the project")}");
        }
    }

    /// <summary>
    /// Takes the channel a word names; false for a word that names none.
    /// </summary>
    public async Task<bool> SetChannelAsync(string word, CancellationToken ct)
    {
        var channel = UpdateChannels.Of(word, string.Empty);
        if (channel.Length == 0)
        {
            return false;
        }

        Channel = channel;
        await _store.SetSettingAsync(SettingKeys.UpdateChannel, channel, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Takes the source a word names; false for a word that names none.
    /// </summary>
    public async Task<bool> SetSourceAsync(string word, CancellationToken ct)
    {
        if (UpdateSources.Choice(word) is not { } choice)
        {
            return false;
        }

        Choice = choice;
        await _store.SetSettingAsync(SettingKeys.UpdateSource, choice, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The sources the list shows and the key of the one a check reads; no list without a baked address.
    /// </summary>
    public (IReadOnlyList<UpdateSourceEntry>? Entries, string Key) Sources(IReadOnlyList<UpdateServer> servers)
    {
        return Url.Length == 0
            ? (null, string.Empty)
            : (UpdateSources.Entries(Url, Origin, servers), UpdateSources.Pick(Url, Origin, Choice, servers).Key);
    }

    /// <summary>
    /// Reads the result the transient install unit left behind.
    /// </summary>
    public void CollectInstallResult()
    {
        var status = TakeStatus(Path.Combine(_directory, "apply.status"));
        if (status is null)
        {
            return;
        }

        var code = int.TryParse(status.Split(' ').FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        if (code == 0)
        {
            _log.Info("update", $"install finished, running {AgentBuild.Version}");
            DropDownloads();
            return;
        }

        Failed = true;
        _log.Warn("update", $"install failed with code {code}; see {Path.Combine(_directory, "apply.log")}");
    }

    /// <summary>
    /// Checks the release manifest for a different version and resolves the packages to fetch.
    /// </summary>
    public async Task<IpcAck> CheckAsync(bool silent, CancellationToken ct)
    {
        if (Url.Length == 0)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateUrlNotSet"));
        }

        if (!silent)
        {
            Checking = true;
            CheckFailed = false;
            await _push(ct).ConfigureAwait(false);
        }

        var faulted = true;
        try
        {
            var ack = await ResolveAsync(ct).ConfigureAwait(false);
            faulted = !ack.Ok;
            return ack;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Failure("update", "the release manifest could not be read", NetworkFailure.Address(_reading), ex);
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateServerUnavailable"));
        }
        finally
        {
            if (!silent)
            {
                Checking = false;
                CheckFailed = faulted;
            }

            await _push(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts downloading the resolved packages and returns at once.
    /// </summary>
    public IpcAck StartDownload(CancellationToken ct)
    {
        var assets = _assets;
        var source = default(CancellationTokenSource);
        lock (_gate)
        {
            if (Downloading)
            {
                return new IpcAck(true, IpcMessage.Key("Agent_UpdateDownloadRunning"));
            }

            if (!Available || assets.Count == 0)
            {
                return new IpcAck(false, IpcMessage.Key("Agent_UpdateNothingToDownload"));
            }

            Downloading = true;
            Downloaded = false;
            Failed = false;
            CancelRequested = false;
            Percent = 0;
            source = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _download = source;
        }

        var version = Version;
        _ = Task.Run(() => RunDownloadAsync(assets, version, source.Token), CancellationToken.None);
        return new IpcAck(true, IpcMessage.Key("Agent_UpdateDownloadStarted", version));
    }

    /// <summary>
    /// Stops a running download and drops its partial files.
    /// </summary>
    public IpcAck Cancel()
    {
        var source = default(CancellationTokenSource);
        lock (_gate)
        {
            if (!Downloading)
            {
                return new IpcAck(false, IpcMessage.Key("Agent_UpdateNoDownloadRunning"));
            }

            CancelRequested = true;
            source = _download;
        }

        source?.Cancel();
        return new IpcAck(true, IpcMessage.Key("Agent_UpdateDownloadCancelled"));
    }

    /// <summary>
    /// Verifies the downloaded packages and lets apt install them from a transient unit.
    /// </summary>
    public async Task<IpcAck> InstallAsync(CancellationToken ct)
    {
        var assets = _assets;
        lock (_gate)
        {
            if (Installing || _applying)
            {
                return new IpcAck(true, IpcMessage.Key("Agent_UpdateInstallRunning"));
            }

            if (!Downloaded || assets.Count == 0)
            {
                return new IpcAck(false, IpcMessage.Key("Agent_UpdateNothingDownloaded"));
            }

            _applying = true;
        }

        try
        {
            return await ApplyAsync(assets, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _applying = false;
            }
        }
    }

    // Verifies the packages and starts the transient unit that installs them.
    private async Task<IpcAck> ApplyAsync(IReadOnlyList<PendingAsset> assets, CancellationToken ct)
    {
        foreach (var asset in assets)
        {
            if (!await VerifyAsync(asset, ct).ConfigureAwait(false))
            {
                Downloaded = false;
                Failed = true;
                await _push(ct).ConfigureAwait(false);
                return new IpcAck(false, IpcMessage.Key("Agent_UpdateVerifyFailed", asset.Name));
            }
        }

        // The install replaces the agent binary and restarts its unit, which would kill an apt run started from
        // this process; the transient unit is the only place it survives.
        var runner = new[] { "/usr/bin/systemd-run", "/bin/systemd-run" }.FirstOrDefault(File.Exists);
        if (runner is null)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateNoTransientUnit", string.Join(' ', assets.Select(a => a.Path))));
        }

        if (_route is { } route)
        {
            _handed = route.Handed(Version);
            await KeepOriginAsync(ct).ConfigureAwait(false);
        }

        var script = WriteApplyScript(assets);
        await Shell.RunAsync("systemctl", ct, "reset-failed", TransientUnit).ConfigureAwait(false);
        var (code, output) = await Shell.RunAsync(runner, ct, $"--unit={TransientUnit}", "--collect", "/bin/sh", script).ConfigureAwait(false);
        if (code != 0)
        {
            Failed = true;
            _log.Warn("update", $"the install unit refused to start: {output}");
            await _push(ct).ConfigureAwait(false);
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateInstallFailed", output));
        }

        Installing = true;
        _log.Info("update", $"installing {Version} from {_directory}");
        await _push(ct).ConfigureAwait(false);
        return new IpcAck(true, IpcMessage.Key("Agent_UpdateInstallStarted", Version));
    }

    // Reads the manifest and keeps the assets matching this architecture and the installed packages.
    private async Task<IpcAck> ResolveAsync(CancellationToken ct)
    {
        var installed = await InstalledPackagesAsync(ct).ConfigureAwait(false);
        if (installed.Count == 0)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateNotPackaged"));
        }

        if (await FetchAsync(ct).ConfigureAwait(false) is not { } release)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateCheckFailed"));
        }

        var arch = await ArchitectureAsync(ct).ConfigureAwait(false);
        var published = UpdateFeed.AssetsFor(release.Manifest, Platform, arch);
        var assets = new List<PendingAsset>();
        foreach (var package in installed)
        {
            var asset = published.FirstOrDefault(a => string.Equals(a.Variant, package, StringComparison.Ordinal));
            if (asset?.Name is not { Length: > 0 } name)
            {
                return new IpcAck(false, IpcMessage.Key("Agent_UpdateNoPackageForArch", package, arch));
            }

            assets.Add(new PendingAsset(
                package,
                name,
                new Uri(release.BaseUrl, name).ToString(),
                asset.Sha256 ?? string.Empty,
                Path.Combine(_directory, name),
                UpdateSources.Fitting(asset, _route!)!));
        }

        Version = release.Manifest.Version ?? string.Empty;
        _files = UpdateSources.Pinned(_route!.Pin) ?? _http;
        _offeredBy = (_route.Url, Channel);
        Description = release.Manifest.Description ?? string.Empty;
        Available = UpdateFeed.IsUpdate(Version, AgentBuild.Version);
        SetupUrl = assets[0].Url;
        Sha256 = assets[0].Sha256;

        // A newly offered version invalidates what was downloaded for the previous one.
        if (Downloaded && !string.Equals(Version, _downloadedVersion, StringComparison.Ordinal))
        {
            DropDownloads();
        }

        _assets = assets;
        _log.Info("update", $"{Version} published for {arch}: {string.Join(", ", assets.Select(a => a.Package))} (installed {AgentBuild.Version})");
        return Available
            ? new IpcAck(true, IpcMessage.Key("Agent_UpdateAvailable", Version))
            : new IpcAck(true, IpcMessage.Key("Agent_UpToDate"));
    }

    // The release the picked source offers on the channel; what kept a server from one is kept for the window.
    private async Task<UpdateRelease?> FetchAsync(CancellationToken ct)
    {
        var route = UpdateSources.Pick(Url, Origin, Choice, await _servers(ct).ConfigureAwait(false));
        _route = route;
        var release = default(UpdateRelease);
        try
        {
            release = await UpdateSources.FetchAsync(_http, route, Channel, url => _reading = url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            NoteFault(route, false, ex);
            ForgetForeign(route);
            throw;
        }

        NoteFault(route, release is not null, null);
        if (release is null)
        {
            ForgetForeign(route);
        }

        return release;
    }

    // A check that found nothing keeps the offer only when it came from the same source on the same channel.
    private void ForgetForeign(UpdateRoute route)
    {
        if (Version.Length == 0 || (string.Equals(_offeredBy.Url, route.Url, StringComparison.OrdinalIgnoreCase) && _offeredBy.Channel == Channel))
        {
            return;
        }

        Available = false;
        Version = string.Empty;
        Description = string.Empty;
        SetupUrl = string.Empty;
        Sha256 = string.Empty;
        _assets = [];
    }

    // Keeps what kept the check of a server from a release; a new failure starts the mark of the window again.
    private void NoteFault(UpdateRoute route, bool found, Exception? error)
    {
        var key = UpdateSources.FaultKey(route, found, error);
        var text = key.Length == 0 ? string.Empty : IpcMessage.Key(key, key == UpdateSources.GoneKey ? route.Gone : route.Host);
        Fault = UpdateSources.Next(Fault, text, DateTimeOffset.UtcNow);
    }

    // Records where the installed packages came from and the source that handed the last ones to apt.
    private async Task KeepOriginAsync(CancellationToken ct)
    {
        await _store.SetSettingAsync(UpdateSources.OriginSetting, Origin.Write(), ct).ConfigureAwait(false);
        await _store.SetSettingAsync(UpdateSources.HandedSetting, _handed?.Write() ?? string.Empty, ct).ConfigureAwait(false);
    }

    private async Task RunDownloadAsync(IReadOnlyList<PendingAsset> assets, string version, CancellationToken ct)
    {
        var reading = string.Empty;
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            Sweep(assets);
            for (var i = 0; i < assets.Count; i++)
            {
                var index = i;
                var asset = assets[index];
                reading = asset.Url;
                if (await VerifyAsync(asset, ct).ConfigureAwait(false))
                {
                    continue;
                }

                await FetchAsync(asset, (done, total) => Report((int)((index * 100 + (total > 0 ? done * 100 / total : 0)) / assets.Count)), ct)
                    .ConfigureAwait(false);
                if (!await VerifyAsync(asset, ct).ConfigureAwait(false))
                {
                    Remove(asset.Path);
                    throw new InvalidOperationException($"{asset.Name}: checksum mismatch");
                }
            }

            Downloaded = true;
            Percent = 100;
            _downloadedVersion = version;
            SetupPath = assets[0].Path;
            _log.Info("update", $"downloaded {version}: {string.Join(", ", assets.Select(a => a.Name))}");
        }
        catch (OperationCanceledException)
        {
            Percent = 0;
            DropPending(assets);
            _log.Info("update", $"download of {version} cancelled");
        }
        catch (Exception ex)
        {
            Failed = true;
            Percent = 0;
            _log.Failure("update", $"download of {version} failed", NetworkFailure.Address(reading), ex);
        }
        finally
        {
            lock (_gate)
            {
                Downloading = false;
                CancelRequested = false;
                _download?.Dispose();
                _download = null;
            }

            await _push(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // The package put together from the installed files and the patch of the release and built again; the whole
    // package, carrying on from its partial, where the release carries no patch from the installed version or the
    // patch does not make the package.
    private async Task FetchAsync(PendingAsset asset, Action<long, long> progress, CancellationToken ct)
    {
        _built.TryRemove(asset.Path, out _);
        if (UpdateFeed.PatchFor(asset.Published, AgentBuild.Version) is { } patch)
        {
            try
            {
                var count = await BuildAsync(asset, patch, progress, ct).ConfigureAwait(false);
                Remove(asset.Path + ".part");
                var deltas = count.Deltas > 0 ? $" ({count.Deltas} of them as deltas of the installed ones)" : string.Empty;
                _log.Info("update", $"{asset.Name}: the patch from {patch.From} carried {count.Carried} of the {count.Files} files{deltas}, "
                    + $"{Megabytes(count.Bytes)} instead of the {Megabytes(asset.Published.Size)} package, the others came from the installed one");
                return;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or UnauthorizedAccessException
                or InvalidOperationException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                _log.Warn("update", $"{asset.Name}: the patch from {patch.From} did not make the package, the whole package is downloaded: {ex.Message}");
            }
        }

        await UpdateDownload.DownloadAsync(_files, new Uri(asset.Url), asset.Path, progress, ct).ConfigureAwait(false);
        RemoveTree(asset);
    }

    // Stages the tree of the package from the patch and builds the package from it with dpkg-deb.
    private async Task<UpdatePatchCount> BuildAsync(PendingAsset asset, UpdatePatchFile patch, Action<long, long> progress, CancellationToken ct)
    {
        var tree = TreeOf(asset);
        var count = await UpdatePatch
            .FetchAsync(_files, new Uri(new Uri(asset.Url), patch.Name!), patch, [Path.Combine("/usr/lib", asset.Package)], tree, progress, ct)
            .ConfigureAwait(false);
        var (code, output) = await TryRunAsync("dpkg-deb", ct, "--root-owner-group", "-Zgzip", "-z1", "--build", tree, asset.Path)
            .ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"dpkg-deb did not build {asset.Name}: {output.Trim()}");
        }

        using (var stream = File.OpenRead(asset.Path))
        {
            _built[asset.Path] = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }

        RemoveTree(asset);
        return count;
    }

    private string TreeOf(PendingAsset asset) => Path.Combine(_directory, Path.GetFileNameWithoutExtension(asset.Name));

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024)).ToString("0.0#", CultureInfo.InvariantCulture) + " MB";

    // Holds the shown percent below 100 until every package is on disk and verified.
    private void Report(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 99);
        if (clamped == Percent)
        {
            return;
        }

        Percent = clamped;
        _ = _push(CancellationToken.None);
    }

    // A package built from a patch is checked against the digest taken when it was built, a downloaded one against
    // the published digest.
    private async Task<bool> VerifyAsync(PendingAsset asset, CancellationToken ct)
    {
        if (!File.Exists(asset.Path))
        {
            return false;
        }

        var expected = _built.TryGetValue(asset.Path, out var built) ? built : asset.Sha256;
        if (expected.Length == 0)
        {
            return true;
        }

        try
        {
            await using var stream = File.OpenRead(asset.Path);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return string.Equals(Convert.ToHexStringLower(hash), expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    // The shell the transient unit runs: apt installs the packages and records its exit code for the next start.
    private string WriteApplyScript(IReadOnlyList<PendingAsset> assets)
    {
        var script = Path.Combine(_directory, "apply.sh");
        var status = Path.Combine(_directory, "apply.status");
        var output = Path.Combine(_directory, "apply.log");
        var files = string.Join(' ', assets.Select(a => $"\"{a.Path}\""));
        var text = string.Join('\n',
            "#!/bin/sh",
            "# Installs the downloaded AmneziaGeo packages and records the result for the agent.",
            $"rm -f \"{status}\"",
            "export DEBIAN_FRONTEND=noninteractive",
            $"apt-get install -y --allow-downgrades --reinstall -o Dpkg::Options::=--force-confold {files} >\"{output}\" 2>&1",
            $"printf '%s %s\\n' \"$?\" \"{Version}\" >\"{status}\"",
            string.Empty);
        File.WriteAllText(script, text);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    // Packages dpkg reports as installed; an agent running outside a package offers no update.
    private static async Task<IReadOnlyList<string>> InstalledPackagesAsync(CancellationToken ct)
    {
        var installed = new List<string>();
        foreach (var package in _packages)
        {
            var (code, output) = await TryRunAsync("dpkg-query", ct, "-W", "-f=${Status}", package).ConfigureAwait(false);
            if (code == 0 && output.Contains("install ok installed", StringComparison.Ordinal))
            {
                installed.Add(package);
            }
        }

        return installed.Count > 0 && installed[0] == CorePackage ? installed : [];
    }

    private static async Task<string> ArchitectureAsync(CancellationToken ct)
    {
        var (code, output) = await TryRunAsync("dpkg", ct, "--print-architecture").ConfigureAwait(false);
        if (code == 0 && output.Trim() is { Length: > 0 } reported)
        {
            return reported;
        }

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "armhf",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
    }

    private static async Task<(int ExitCode, string Output)> TryRunAsync(string file, CancellationToken ct, params string[] args)
    {
        try
        {
            return await Shell.RunAsync(file, ct, args).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return (-1, string.Empty);
        }
    }

    private static string? TakeStatus(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            File.Delete(path);
            return text;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void DropDownloads()
    {
        Downloaded = false;
        Percent = 0;
        SetupPath = string.Empty;
        _downloadedVersion = string.Empty;
        _built.Clear();
        Sweep([]);
    }

    // Drops what earlier downloads left for packages other than these, the result of the last install aside.
    private void Sweep(IReadOnlyList<PendingAsset> assets)
    {
        try
        {
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(_directory).ToList())
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith("apply.", StringComparison.Ordinal)
                    || assets.Any(asset => name.StartsWith(Path.GetFileNameWithoutExtension(asset.Name), StringComparison.Ordinal)))
                {
                    continue;
                }

                if (System.IO.Directory.Exists(entry))
                {
                    System.IO.Directory.Delete(entry, recursive: true);
                }
                else
                {
                    Remove(entry);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // A cancel drops everything fetched for the packages.
    private void DropPending(IReadOnlyList<PendingAsset> assets)
    {
        foreach (var asset in assets)
        {
            _built.TryRemove(asset.Path, out _);
            Remove(asset.Path + ".part");
            Remove(asset.Path);
            RemoveTree(asset);
        }
    }

    private void RemoveTree(PendingAsset asset)
    {
        var tree = TreeOf(asset);
        Remove(tree + UpdatePatch.Extension);
        Remove(tree + UpdatePatch.Extension + ".part");
        try
        {
            if (System.IO.Directory.Exists(tree))
            {
                System.IO.Directory.Delete(tree, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _download?.Cancel();
            _download?.Dispose();
            _download = null;
        }
    }

    private sealed record PendingAsset(string Package, string Name, string Url, string Sha256, string Path, UpdateAsset Published);
}
