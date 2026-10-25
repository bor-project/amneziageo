using System.Net.Http;
using System.Runtime.InteropServices;
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
/// Application update for the Android package: reads the release manifest, downloads the APK published for this
/// processor type into the private cache, and hands it to the system installer, which replaces this very package.
/// </summary>
internal sealed class AndroidUpdater : IDisposable
{
    private const string Platform = "android";
    private const string SessionEntry = "package";

    private static readonly string _arch = UpdateFeed.AndroidArch(RuntimeInformation.ProcessArchitecture);

    private readonly HttpClient _http;
    private readonly AndroidAgentLog _log;
    private string _reading = string.Empty;
    private readonly Action _push;
    private readonly string _installed;
    private readonly Func<IReadOnlyList<UpdateServer>> _servers;
    private readonly Action _keep;
    private readonly string _directory;
    private readonly Lock _gate = new();

    private string _name = string.Empty;
    private string _offeredVersion = string.Empty;
    private UpdateRoute? _route;

    // The source and the channel the release on offer came from.
    private (string Url, string Channel) _offeredBy = (string.Empty, string.Empty);

    // The client the package of the last release found is read with, under the pin of its source.
    private HttpClient _files;
    private CancellationTokenSource? _download;
    private bool _disposed;
    private bool _applying;

    /// <summary>
    /// ctor
    /// </summary>
    public AndroidUpdater(HttpClient http, AndroidAgentLog log, Action push, string installed, Func<IReadOnlyList<UpdateServer>> servers, Action keep)
    {
        _http = http;
        _files = http;
        _log = log;
        _push = push;
        _installed = installed;
        _servers = servers;
        _keep = keep;
        _directory = System.IO.Path.Combine(
            Application.Context.CacheDir?.AbsolutePath ?? System.IO.Path.GetTempPath(),
            "updates");
        UpdateInstallReceiver.Finished = OnInstallFinished;
        UpdateFound.Settle(Application.Context);
    }

    /// <summary>
    /// Update metadata URL baked into this build; empty hides the update section.
    /// </summary>
    public string Url => AndroidBuild.UpdateUrl;

    /// <summary>
    /// Whether the check also offers prereleases.
    /// </summary>
    public bool AllowPrerelease => UpdateChannels.Prerelease(Channel);

    /// <summary>
    /// The channel the releases come in.
    /// </summary>
    public string Channel { get; set; } = UpdateChannels.Of(AndroidBuild.AllowPrerelease);

    /// <summary>
    /// The source picked by hand: origin or the address of a server; empty while the servers decide.
    /// </summary>
    public string Choice { get; set; } = string.Empty;

    /// <summary>
    /// Where the installed package came from.
    /// </summary>
    public UpdateOrigin Origin { get; set; } = UpdateOrigin.Baked;

    /// <summary>
    /// The source that handed the last package to the installer, null while none waits to run.
    /// </summary>
    public UpdateOrigin? Handed { get; set; }

    /// <summary>
    /// What kept the last check of a server from a release.
    /// </summary>
    public UpdateFault Fault { get; private set; } = UpdateFault.None;

    /// <summary>
    /// Makes the source that handed the package to the installer the origin once its version runs; answers whether
    /// the origin changed.
    /// </summary>
    public bool Settle()
    {
        var handed = Handed;
        (Origin, Handed) = UpdateSources.Settle(Origin, handed, _installed);
        return !ReferenceEquals(Handed, handed);
    }

    /// <summary>
    /// The sources the list shows and the key of the one a check reads; no list without a baked address.
    /// </summary>
    public (IReadOnlyList<UpdateSourceEntry>? Entries, string Key) Sources()
    {
        var servers = _servers();
        return Url.Length == 0
            ? (null, string.Empty)
            : (UpdateSources.Entries(Url, Origin, servers), UpdateSources.Pick(Url, Origin, Choice, servers).Key);
    }

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
        var sha = Sha256;

        // Гаснущий экран замораживает процесс вместе с загрузкой; служба переднего плана держит его до конца.
        UpdateDownloadService.Start(Application.Context);
        _ = Task.Run(() => RunDownloadAsync(url, name, version, sha, source.Token), CancellationToken.None);
        return new IpcAck(true, IpcMessage.Key("Agent_UpdateDownloadStarted", version));
    }

    /// <summary>
    /// Stops a running download and drops its partial file.
    /// </summary>
    public IpcAck Cancel()
    {
        if (!Downloading)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateNoDownloadRunning"));
        }

        StopDownload();
        return new IpcAck(true, IpcMessage.Key("Agent_UpdateDownloadCancelled"));
    }

    /// <summary>
    /// Verifies the downloaded package and opens an install session for it.
    /// </summary>
    public async Task<IpcAck> InstallAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (Installing || _applying)
            {
                return new IpcAck(true, IpcMessage.Key("Agent_UpdateInstallRunning"));
            }

            if (!Downloaded || SetupPath.Length == 0)
            {
                return new IpcAck(false, IpcMessage.Key("Agent_UpdateNothingDownloaded"));
            }

            _applying = true;
        }

        try
        {
            return await ApplyAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _applying = false;
            }
        }
    }

    // Verifies the package and opens an install session for it.
    private async Task<IpcAck> ApplyAsync(CancellationToken ct)
    {
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
            if (_route is { } route)
            {
                Handed = route.Handed(Version);
                _keep();
            }

            _push();

            // Установку начал человек из окна, поэтому после замены пакета окно возвращается.
            UpdateReopen.Arm(context);
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

        var asset = UpdateFeed.AndroidAsset(release.Manifest, _arch);
        if (asset?.Name is not { Length: > 0 } name)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateNoPackageForArch", Platform, _arch));
        }

        Version = release.Manifest.Version ?? string.Empty;
        Description = release.Manifest.Description ?? string.Empty;
        Available = UpdateFeed.IsUpdate(Version, _installed);
        SetupUrl = new Uri(release.BaseUrl, name).ToString();
        _files = UpdateSources.Pinned(_route!.Pin) ?? _http;
        _offeredBy = (_route.Url, Channel);
        Sha256 = asset.Sha256 ?? string.Empty;
        _name = name;

        // Другая версия обесценивает всё, что скачано для прошлой: идущая загрузка останавливается, готовый
        // пакет и черновик уходят. Первая сверка после запуска версию не меняет - иначе чужой процесс уносил бы
        // черновик, который как раз и предстоит докачать.
        var changed = _offeredVersion.Length > 0 && !string.Equals(Version, _offeredVersion, StringComparison.Ordinal);
        _offeredVersion = Version;
        if (changed)
        {
            StopDownload();
            DropDownloads();
        }

        // В каталоге остаётся только предложенный релиз: остальное падало бы на сверке хеша.
        SweepDownloads(FileFor(Version, name));
        await AdoptAsync(FileFor(Version, name), ct).ConfigureAwait(false);

        _log.Info("update", $"{Version} published as {name} for this {_arch} device (installed {_installed})");
        UpdateFound.Tell(Application.Context, Available ? Version : string.Empty);
        return Available
            ? new IpcAck(true, IpcMessage.Key("Agent_UpdateAvailable", Version))
            : new IpcAck(true, IpcMessage.Key("Agent_UpToDate"));
    }

    // The release the picked source offers on the channel; what kept a server from one is kept for the window.
    private async Task<UpdateRelease?> FetchAsync(CancellationToken ct)
    {
        var route = UpdateSources.Pick(Url, Origin, Choice, _servers());
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
        _name = string.Empty;
        UpdateFound.Tell(Application.Context, string.Empty);
    }

    // Keeps what kept the check of a server from a release; a new failure starts the mark of the window again.
    private void NoteFault(UpdateRoute route, bool found, Exception? error)
    {
        var key = UpdateSources.FaultKey(route, found, error);
        var text = key.Length == 0 ? string.Empty : IpcMessage.Key(key, key == UpdateSources.GoneKey ? route.Gone : route.Host);
        Fault = UpdateSources.Next(Fault, text, DateTimeOffset.UtcNow);
    }

    private async Task RunDownloadAsync(string url, string name, string version, string sha, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(_directory, FileFor(version, name));
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            await DownloadFileAsync(url, path, ct).ConfigureAwait(false);

            // Хеш берётся тот, что был объявлен на старте: за время загрузки сверка могла предложить другой
            // релиз, и целый пакет объявлялся бы битым.
            if (!await VerifyAsync(path, sha, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"{name}: checksum mismatch");
            }

            Downloaded = true;
            Percent = 100;
            SetupPath = path;
            _log.Info("update", $"downloaded {version}: {name}");
        }
        catch (System.OperationCanceledException)
        {
            // Отмену просил человек либо смена версии - в обоих случаях докачивать нечего.
            Percent = 0;
            DropPending(path);
            _log.Info("update", $"download of {version} cancelled");
        }
        catch (Exception ex)
        {
            // Черновик остаётся: обрыв связи не повод качать сначала.
            Failed = true;
            Percent = 0;
            Remove(path);
            _log.Failure("update", $"download of {version} failed", NetworkFailure.Address(url), ex);
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

            UpdateDownloadService.Stop(Application.Context);
            _push();
        }
    }

    // Продолжает черновик с того байта, на котором его бросили: заголовок Range, дозапись в конец файла.
    private async Task DownloadFileAsync(string url, string path, CancellationToken ct)
    {
        var partial = path + ".part";
        var have = DraftLength(partial);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        }

        using var response = await _files.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Черновик длиннее самого пакета - он не от этого файла; начинаем заново.
        if (have > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            Remove(partial);
            await DownloadFileAsync(url, path, ct).ConfigureAwait(false);
            return;
        }

        // Диапазон приняли только при 206; на обычный ответ черновик обнуляется.
        var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed)
        {
            response.EnsureSuccessStatusCode();
            have = 0;
        }

        var length = (response.Content.Headers.ContentLength ?? 0) + have;
        var buffer = new byte[81920];
        var written = have;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new System.IO.FileStream(
            partial,
            resumed ? System.IO.FileMode.Append : System.IO.FileMode.Create,
            System.IO.FileAccess.Write))
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

    private static long DraftLength(string partial)
    {
        try
        {
            var file = new System.IO.FileInfo(partial);
            return file.Exists ? file.Length : 0;
        }
        catch (System.IO.IOException)
        {
            return 0;
        }
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

    // Целый пакет этой версии уже лежит в кеше: после перезапуска приложения качать его снова незачем.
    private async Task AdoptAsync(string file, CancellationToken ct)
    {
        if (!Available || Downloading || Downloaded)
        {
            return;
        }

        var path = System.IO.Path.Combine(_directory, file);
        if (!await VerifyAsync(path, Sha256, ct).ConfigureAwait(false))
        {
            return;
        }

        Downloaded = true;
        Percent = 100;
        SetupPath = path;
        _log.Info("update", $"{Version} is already in the cache: {file}");
    }

    // Имя пакета помечено версией: черновик прошлого релиза иначе дописывался бы в нынешний.
    private static string FileFor(string version, string name)
    {
        return version.Length == 0 ? name : version + "-" + name;
    }

    private void DropDownloads()
    {
        Downloaded = false;
        Percent = 0;
        SetupPath = string.Empty;
        SweepDownloads(string.Empty);
    }

    // Оставляет пакет предложенной версии и его черновик, остальное удаляет.
    private void SweepDownloads(string keep)
    {
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory))
            {
                var name = System.IO.Path.GetFileName(file);
                if (keep.Length == 0
                    || (!string.Equals(name, keep, StringComparison.Ordinal)
                        && !string.Equals(name, keep + ".part", StringComparison.Ordinal)))
                {
                    Remove(file);
                }
            }
        }
        catch (System.IO.DirectoryNotFoundException)
        {
        }
        catch (System.IO.IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Останавливает загрузку без ответа вызывающему: смена версии обрывает её сама.
    private void StopDownload()
    {
        var source = default(CancellationTokenSource);
        lock (_gate)
        {
            if (!Downloading)
            {
                return;
            }

            CancelRequested = true;
            source = _download;
        }

        source?.Cancel();
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
