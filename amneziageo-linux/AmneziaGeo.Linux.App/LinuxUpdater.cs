using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Application update for the Debian packages: reads the release manifest, downloads the packages this machine
/// carries, and hands them to apt in a transient unit that outlives the agent restart the install triggers.
/// </summary>
internal sealed class LinuxUpdater : IDisposable
{
    private const string CorePackage = "amneziageo";
    private const string GuiPackage = "amneziageo-gui";
    private const string TransientUnit = "amneziageo-update";
    private const string Platform = "linux";
    private const string UserAgent = "AmneziaGeo-UpdateChecker";

    private static readonly string[] _packages = [CorePackage, GuiPackage];

    private readonly HttpClient _http;
    private readonly AgentLog _log;
    private readonly Func<CancellationToken, Task> _push;
    private readonly string _directory = Path.Combine(AgentPaths.Root, "updates");
    private readonly Lock _gate = new();

    private IReadOnlyList<PendingAsset> _assets = [];
    private CancellationTokenSource? _download;
    private string _downloadedVersion = string.Empty;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public LinuxUpdater(HttpClient http, AgentLog log, Func<CancellationToken, Task> push)
    {
        _http = http;
        _log = log;
        _push = push;
    }

    /// <summary>
    /// Update metadata URL baked into this build; empty hides the update section.
    /// </summary>
    public string Url => AgentBuild.UpdateUrl;

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
            _log.Error("update", "the release manifest could not be read", ex);
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
        if (!Downloaded || assets.Count == 0)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateNothingDownloaded"));
        }

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
                Path.Combine(_directory, name)));
        }

        Version = release.Manifest.Version ?? string.Empty;
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

    // Newest release on the prerelease channel, the stable manifest otherwise.
    private async Task<(UpdateManifest Manifest, Uri BaseUrl)?> FetchAsync(CancellationToken ct)
    {
        if (AgentBuild.AllowPrerelease && UpdateFeed.TryGitHubRepo(Url, out var owner, out var repo))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateFeed.ReleasesUrl(owner, repo));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode
                && UpdateFeed.SelectManifestUrl(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is { } picked
                && UpdateFeed.ParseManifest(await _http.GetStringAsync(picked, ct).ConfigureAwait(false)) is { } prerelease)
            {
                return (prerelease, new Uri(picked));
            }
        }

        var json = await _http.GetStringAsync(Url, ct).ConfigureAwait(false);
        return UpdateFeed.ParseManifest(json) is { } manifest ? (manifest, new Uri(Url)) : null;
    }

    private async Task RunDownloadAsync(IReadOnlyList<PendingAsset> assets, string version, CancellationToken ct)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            for (var i = 0; i < assets.Count; i++)
            {
                var index = i;
                await DownloadFileAsync(assets[index], p => Report((index * 100 + p) / assets.Count), ct).ConfigureAwait(false);
                if (!await VerifyAsync(assets[index], ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"{assets[index].Name}: checksum mismatch");
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
            DropPending(assets);
            _log.Error("update", $"download of {version} failed", ex);
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

    private async Task DownloadFileAsync(PendingAsset asset, Action<int> progress, CancellationToken ct)
    {
        var partial = asset.Path + ".part";
        using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength ?? 0;
        var buffer = new byte[81920];
        var written = 0L;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(partial))
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            while (read > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (length > 0)
                {
                    progress((int)(written * 100 / length));
                }

                read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
        }

        File.Move(partial, asset.Path, true);
    }

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

    private static async Task<bool> VerifyAsync(PendingAsset asset, CancellationToken ct)
    {
        if (!File.Exists(asset.Path))
        {
            return false;
        }

        if (asset.Sha256.Length == 0)
        {
            return true;
        }

        try
        {
            await using var stream = File.OpenRead(asset.Path);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return string.Equals(Convert.ToHexStringLower(hash), asset.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
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
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.deb"))
            {
                Remove(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Nothing half-fetched or unverified is kept: the next attempt starts from scratch.
    private static void DropPending(IReadOnlyList<PendingAsset> assets)
    {
        foreach (var asset in assets)
        {
            Remove(asset.Path + ".part");
            Remove(asset.Path);
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

    private sealed record PendingAsset(string Package, string Name, string Url, string Sha256, string Path);
}
