using System.Net.Http;
using System.Security.Cryptography;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Application update for the Android package: reads the release manifest, downloads the published APK into the
/// private cache, and hands it to the system installer, which replaces this very package.
/// </summary>
internal sealed class AndroidUpdater : IDisposable
{
    private const string Platform = "android";
    private const string Arch = "universal";
    private const string UserAgent = "AmneziaGeo-UpdateChecker";
    private const string SessionEntry = "package";

    private readonly HttpClient _http;
    private readonly AndroidAgentLog _log;
    private readonly Action _push;
    private readonly string _installed;
    private readonly string _directory;
    private readonly Lock _gate = new();

    private string _name = string.Empty;
    private string _downloadedVersion = string.Empty;
    private CancellationTokenSource? _download;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public AndroidUpdater(HttpClient http, AndroidAgentLog log, Action push, string installed)
    {
        _http = http;
        _log = log;
        _push = push;
        _installed = installed;
        _directory = System.IO.Path.Combine(
            Application.Context.CacheDir?.AbsolutePath ?? System.IO.Path.GetTempPath(),
            "updates");
        UpdateInstallReceiver.Finished = OnInstallFinished;
    }

    /// <summary>
    /// Update metadata URL baked into this build; empty hides the update section.
    /// </summary>
    public string Url => AndroidBuild.UpdateUrl;

    /// <summary>
    /// Whether the check also offers prereleases.
    /// </summary>
    public bool AllowPrerelease { get; set; } = AndroidBuild.AllowPrerelease;

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
    /// Download URL of the package.
    /// </summary>
    public string SetupUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Published SHA-256 of the package.
    /// </summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>
    /// Path of the downloaded package.
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
    /// Whether the package is downloading.
    /// </summary>
    public bool Downloading { get; private set; }

    /// <summary>
    /// Whether the package is downloaded and verified.
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
    /// Whether the system installer holds the downloaded package.
    /// </summary>
    public bool Installing { get; private set; }

    /// <summary>
    /// Checks the release manifest for a different version and resolves the package to fetch.
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
            _push();
        }

        var faulted = true;
        try
        {
            var ack = await ResolveAsync(ct).ConfigureAwait(false);
            faulted = !ack.Ok;
            return ack;
        }
        catch (Exception ex) when (ex is not System.OperationCanceledException)
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

            _push();
        }
    }

    /// <summary>
    /// Starts downloading the resolved package and returns at once.
    /// </summary>
    public IpcAck StartDownload(CancellationToken ct)
    {
        var source = default(CancellationTokenSource);
        lock (_gate)
        {
            if (Downloading)
            {
                return new IpcAck(true, IpcMessage.Key("Agent_UpdateDownloadRunning"));
            }

            if (!Available || SetupUrl.Length == 0)
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
        var url = SetupUrl;
        var name = _name;
        _ = Task.Run(() => RunDownloadAsync(url, name, version, source.Token), CancellationToken.None);
        return new IpcAck(true, IpcMessage.Key("Agent_UpdateDownloadStarted", version));
    }

    /// <summary>
    /// Stops a running download and drops its partial file.
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
    /// Verifies the downloaded package and opens an install session for it.
    /// </summary>
    public async Task<IpcAck> InstallAsync(CancellationToken ct)
    {
        if (!Downloaded || SetupPath.Length == 0)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateNothingDownloaded"));
        }

        if (!await VerifyAsync(SetupPath, Sha256, ct).ConfigureAwait(false))
        {
            Downloaded = false;
            Failed = true;
            _push();
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateVerifyFailed", _name));
        }

        var context = Application.Context;
        if (!CanInstall(context))
        {
            OfferInstallSources(context);
            return new IpcAck(false, IpcMessage.Key("Android_UpdateAllowInstallSources"));
        }

        try
        {
            Installing = true;
            Failed = false;
            _push();
            var session = await CommitAsync(context, ct).ConfigureAwait(false);
            _log.Info("update", $"installing {Version} in session {session}");
            return new IpcAck(true, IpcMessage.Key("Agent_UpdateInstallStarted", Version));
        }
        catch (Exception ex)
        {
            Installing = false;
            Failed = true;
            _log.Error("update", $"the install of {Version} could not be started", ex);
            _push();
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateInstallFailed", ex.Message));
        }
    }

    // Reads the manifest and keeps the package published for this platform.
    private async Task<IpcAck> ResolveAsync(CancellationToken ct)
    {
        if (await FetchAsync(ct).ConfigureAwait(false) is not { } release)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateCheckFailed"));
        }

        var asset = UpdateFeed.AssetsFor(release.Manifest, Platform, Arch).FirstOrDefault();
        if (asset?.Name is not { Length: > 0 } name)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateNoPackageForArch", Platform, Arch));
        }

        Version = release.Manifest.Version ?? string.Empty;
        Description = release.Manifest.Description ?? string.Empty;
        Available = UpdateFeed.IsUpdate(Version, _installed);
        SetupUrl = new Uri(release.BaseUrl, name).ToString();
        Sha256 = asset.Sha256 ?? string.Empty;
        _name = name;

        // A newly offered version invalidates what was downloaded for the previous one.
        if (Downloaded && !string.Equals(Version, _downloadedVersion, StringComparison.Ordinal))
        {
            DropDownloads();
        }

        _log.Info("update", $"{Version} published as {name} (installed {_installed})");
        return Available
            ? new IpcAck(true, IpcMessage.Key("Agent_UpdateAvailable", Version))
            : new IpcAck(true, IpcMessage.Key("Agent_UpToDate"));
    }

    // Newest release on the prerelease channel, the stable manifest otherwise.
    private async Task<(UpdateManifest Manifest, Uri BaseUrl)?> FetchAsync(CancellationToken ct)
    {
        if (AllowPrerelease && UpdateFeed.TryGitHubRepo(Url, out var owner, out var repo))
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

    private async Task RunDownloadAsync(string url, string name, string version, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(_directory, name);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            await DownloadFileAsync(url, path, ct).ConfigureAwait(false);
            if (!await VerifyAsync(path, Sha256, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"{name}: checksum mismatch");
            }

            Downloaded = true;
            Percent = 100;
            SetupPath = path;
            _downloadedVersion = version;
            _log.Info("update", $"downloaded {version}: {name}");
        }
        catch (System.OperationCanceledException)
        {
            Percent = 0;
            DropPending(path);
            _log.Info("update", $"download of {version} cancelled");
        }
        catch (Exception ex)
        {
            Failed = true;
            Percent = 0;
            DropPending(path);
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

            _push();
        }
    }

    private async Task DownloadFileAsync(string url, string path, CancellationToken ct)
    {
        var partial = path + ".part";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength ?? 0;
        var buffer = new byte[81920];
        var written = 0L;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = System.IO.File.Create(partial))
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            while (read > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (length > 0)
                {
                    Report((int)(written * 100 / length));
                }

                read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
        }

        System.IO.File.Move(partial, path, true);
    }

    // Holds the shown percent below 100 until the package is on disk and verified.
    private void Report(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 99);
        if (clamped == Percent)
        {
            return;
        }

        Percent = clamped;
        _push();
    }

    // Copies the package into a session of the system installer and commits it; the outcome arrives at the receiver.
    private async Task<int> CommitAsync(Context context, CancellationToken ct)
    {
        var installer = context.PackageManager?.PackageInstaller
            ?? throw new InvalidOperationException("the system has no package installer");
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName);
        var id = installer.CreateSession(parameters);
        var session = installer.OpenSession(id);
        try
        {
            await using (var source = System.IO.File.OpenRead(SetupPath))
            await using (var target = session.OpenWrite(SessionEntry, 0, source.Length))
            {
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
                session.Fsync(target);
            }

            session.Commit(StatusIntent(context, id).IntentSender!);
            return id;
        }
        catch
        {
            installer.AbandonSession(id);
            throw;
        }
        finally
        {
            session.Close();
        }
    }

    // Where the installer reports back; it fills the extras itself, so the intent has to stay mutable.
    private static PendingIntent StatusIntent(Context context, int sessionId)
    {
        var intent = new Intent(context, typeof(UpdateInstallReceiver));
        var flags = PendingIntentFlags.UpdateCurrent;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
        {
            flags |= PendingIntentFlags.Mutable;
        }

        return PendingIntent.GetBroadcast(context, sessionId, intent, flags)
            ?? throw new InvalidOperationException("the installer callback could not be created");
    }

    private void OnInstallFinished(bool installed, string message)
    {
        Installing = false;
        if (installed)
        {
            _log.Info("update", $"installed {Version}");
            DropDownloads();
        }
        else
        {
            Failed = true;
            _log.Warn("update", $"the install of {Version} did not go through: {message}");
        }

        _push();
    }

    // Whether the user lets this application install packages; the installer turns the session down without it.
    private static bool CanInstall(Context context)
    {
        return Build.VERSION.SdkInt < BuildVersionCodes.O
            || context.PackageManager?.CanRequestPackageInstalls() == true;
    }

    // Opens the screen that grants it, so the next press of Install goes through.
    private void OfferInstallSources(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        try
        {
            var intent = new Intent(
                Settings.ActionManageUnknownAppSources,
                global::Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            _log.Warn("update", "this device has no install-sources screen: " + ex.Message);
        }
    }

    private static async Task<bool> VerifyAsync(string path, string expected, CancellationToken ct)
    {
        if (!System.IO.File.Exists(path))
        {
            return false;
        }

        if (expected.Length == 0)
        {
            return true;
        }

        try
        {
            await using var stream = System.IO.File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return string.Equals(Convert.ToHexStringLower(hash), expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (System.IO.IOException)
        {
            return false;
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
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.apk"))
            {
                Remove(file);
            }
        }
        catch (System.IO.IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Nothing half-fetched or unverified is kept: the next attempt starts from scratch.
    private static void DropPending(string path)
    {
        Remove(path + ".part");
        Remove(path);
    }

    private static void Remove(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (System.IO.IOException)
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
        UpdateInstallReceiver.Finished = null;
        lock (_gate)
        {
            _download?.Cancel();
            _download?.Dispose();
            _download = null;
        }
    }
}
