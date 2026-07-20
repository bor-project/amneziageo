using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// The agent presence link: a background thread that keeps the status pipe attached (announcing UI presence so
/// the tunnel stays up while the GUI is closed), maps each snapshot to a tray state, and can send a disconnect.
/// Kept deliberately free of reflection JSON so the native image stays small: commands are literal strings and
/// snapshots are read field-by-field with Utf8JsonReader. Protocol mirrors AmneziaGeo.Ipc (IpcContract / IpcJson).
/// </summary>
internal static class AgentLink
{
    // IpcContract.PipeName.
    private const string PipeName = "AmneziaGeo.Agent";

    // IpcContract.OpAttachUi / OpSetConnection / OpCheckUpdate, camelCase per IpcJson naming policy.
    private const string AttachCommand = "{\"type\":\"command\",\"command\":{\"op\":\"attach-ui\",\"args\":[]}}";
    private const string ConnectCommand = "{\"type\":\"command\",\"command\":{\"op\":\"set-connection\",\"args\":[\"connect\"]}}";
    private const string DisconnectCommand = "{\"type\":\"command\",\"command\":{\"op\":\"set-connection\",\"args\":[\"disconnect\"]}}";
    private const string CheckUpdateCommand = "{\"type\":\"command\",\"command\":{\"op\":\"check-update\",\"args\":[]}}";
    private const string CancelDownloadCommand = "{\"type\":\"command\",\"command\":{\"op\":\"cancel-download\",\"args\":[]}}";

    private static readonly UTF8Encoding _utf8 = new(false);
    private static volatile StreamWriter? _writer;
    private static Action<int, bool, bool, bool, bool>? _onState;
    private static Action<bool, string>? _onUpdate;
    private static Action? _onDownloaded;
    private static Action? _onDownloadFailed;
    private static Action? _onCheckFinished;

    // Persists across pipe reconnects so a still-latched download failure does not re-fire the balloon each time
    // the tray reconnects to the agent (#8).
    private static bool _prevDownloadFailed;

    /// <summary>
    /// Whether the agent has an active profile selected/bound, so a connect can be issued from the tray.
    /// </summary>
    public static volatile bool HasActiveProfile;

    /// <summary>
    /// Whether tray notifications are enabled. Defaults to true so an older agent still notifies.
    /// </summary>
    public static volatile bool ShowNotifications = true;

    /// <summary>
    /// Whether a snapshot has actually been parsed, so the settings above carry agent values rather than their
    /// defaults. A dropped link pushes a synthetic disconnected state without one.
    /// </summary>
    public static volatile bool SnapshotSeen;

    /// <summary>
    /// Whether the agent reports an application update available.
    /// </summary>
    public static volatile bool UpdateAvailable;

    /// <summary>
    /// Whether the setup for the available version is downloaded and ready to install.
    /// </summary>
    public static volatile bool UpdateDownloaded;

    /// <summary>
    /// Whether a setup download is currently running, so the tray can confirm before an exit cancels it (#21).
    /// </summary>
    public static volatile bool DownloadInProgress;

    /// <summary>
    /// Current setup download progress in percent (0..100), shown in the tray menu while downloading (#17).
    /// </summary>
    public static volatile int DownloadPercent;

    /// <summary>
    /// Whether the last setup download failed; its rising edge fires the tray warning balloon (#8).
    /// </summary>
    public static volatile bool DownloadFailed;

    /// <summary>
    /// Whether a manual update check is running, so the menu shows a checking state and blocks a second check (#15).
    /// </summary>
    public static volatile bool CheckInProgress;

    /// <summary>
    /// Whether the last manual update check failed, so the up-to-date notice is suppressed (#15).
    /// </summary>
    public static volatile bool CheckFailed;

    /// <summary>
    /// The available update version, empty when none.
    /// </summary>
    public static volatile string UpdateVersion = string.Empty;

    /// <summary>
    /// Whether the build has an update URL configured; without one there is nothing to check against.
    /// </summary>
    public static volatile bool HasUpdateUrl;

    /// <summary>
    /// Starts the connection loop; <paramref name="onState"/> receives 0 (disconnected) / 1 (transitioning) /
    /// 2 (connected), whether the agent latched a connect failure, whether the transition is a user
    /// disconnect (the agent reports "disconnecting" only for that, not for a re-dial), whether the last
    /// disconnect failed to complete, and whether this is a synthetic link-lost push (so lifecycle balloons are
    /// suppressed, since the tunnel state is then unknown); <paramref name="onUpdate"/>
    /// receives the update-available flag and version whenever they change; <paramref name="onDownloaded"/> fires
    /// when a download completes (the ready-to-install edge); <paramref name="onDownloadFailed"/> fires on the
    /// download-failure edge; <paramref name="onCheckFinished"/> fires when a manual update check finishes (the
    /// checking-flag falling edge). All fire off a background thread.
    /// </summary>
    public static void Start(Action<int, bool, bool, bool, bool> onState, Action<bool, string> onUpdate, Action onDownloaded, Action onDownloadFailed, Action onCheckFinished)
    {
        _onState = onState;
        _onUpdate = onUpdate;
        _onDownloaded = onDownloaded;
        _onDownloadFailed = onDownloadFailed;
        _onCheckFinished = onCheckFinished;
        var thread = new Thread(Loop) { IsBackground = true, Name = "agent-link" };
        thread.Start();
    }

    /// <summary>
    /// Tells the agent to bring the tunnel up (best-effort; ignored when the link is down).
    /// </summary>
    public static void SendConnect()
    {
        try
        {
            _writer?.WriteLine(ConnectCommand);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Tells the agent to tear the tunnel down (best-effort; ignored when the link is down).
    /// </summary>
    public static void SendDisconnect()
    {
        try
        {
            _writer?.WriteLine(DisconnectCommand);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Asks the agent to check for an application update now (best-effort; the result rides the next snapshot).
    /// </summary>
    public static void SendCheckUpdate()
    {
        try
        {
            _writer?.WriteLine(CheckUpdateCommand);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Asks the agent to cancel a running setup download (best-effort; relayed to the UI that owns the byte-pump).
    /// </summary>
    public static void SendCancelDownload()
    {
        try
        {
            _writer?.WriteLine(CancelDownloadCommand);
        }
        catch
        {
        }
    }

    private static void Loop()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(3000);
                using var writer = new StreamWriter(pipe, _utf8) { AutoFlush = true };
                using var reader = new StreamReader(pipe, _utf8);
                _writer = writer;
                writer.WriteLine(AttachCommand);

                var prevUpdateAvail = false;
                var prevUpdateVer = string.Empty;
                var prevDownloaded = false;
                var prevChecking = false;
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length > 0 && TryReadState(line, out var state, out var connectFailed, out var disconnecting, out var disconnectFailed))
                    {
                        _onState?.Invoke(state, connectFailed, disconnecting, disconnectFailed, false);
                        if (UpdateAvailable != prevUpdateAvail || !string.Equals(UpdateVersion, prevUpdateVer, StringComparison.Ordinal))
                        {
                            prevUpdateAvail = UpdateAvailable;
                            prevUpdateVer = UpdateVersion;
                            _onUpdate?.Invoke(UpdateAvailable, UpdateVersion);
                        }

                        // Ready-to-install edge; the handler dedups by version so a reconnect over a still-ready
                        // download does not re-announce it.
                        if (UpdateDownloaded && !prevDownloaded)
                        {
                            _onDownloaded?.Invoke();
                        }

                        prevDownloaded = UpdateDownloaded;

                        // Download-failure edge (#8); the agent clears the flag when a download starts, so a later
                        // failure fires again. The prev flag is static so a reconnect over a still-latched failure
                        // does not re-warn.
                        if (DownloadFailed && !_prevDownloadFailed)
                        {
                            _onDownloadFailed?.Invoke();
                        }

                        _prevDownloadFailed = DownloadFailed;

                        // Manual-check finished edge (#15): the checking flag falling within this live session
                        // announces the up-to-date result. A fresh connection starts prevChecking false, so a
                        // check that completed during a link outage cannot spuriously fire it.
                        if (prevChecking && !CheckInProgress)
                        {
                            _onCheckFinished?.Invoke();
                        }

                        prevChecking = CheckInProgress;
                    }
                }
            }
            catch
            {
                // fall through to reconnect
            }

            _writer = null;
            HasActiveProfile = false;
            DownloadInProgress = false;
            DownloadPercent = 0;
            CheckInProgress = false;
            _onState?.Invoke(0, false, false, false, true);
            Thread.Sleep(2000);
        }
    }

    // Maps a snapshot line to the three-way tray state, matching ConnectionViewModel.ConnState, plus the agent's
    // latched connect-failure flag (set by FailConnect, cleared on any user connect/disconnect).
    private static bool TryReadState(string json, out int state, out bool connectFailed, out bool disconnecting, out bool disconnectFailed)
    {
        state = 0;
        connectFailed = false;
        disconnecting = false;
        disconnectFailed = false;
        var status = default(string);
        var active = true;
        var haveStatus = false;
        var selected = default(string);
        var bound = default(string);
        var notify = true;
        var updateAvail = false;
        var updateDownloaded = false;
        var updateDownloading = false;
        var updateDownloadPercent = 0;
        var updateFailed = false;
        var updateChecking = false;
        var updateCheckFailed = false;
        var updateVer = default(string);
        var updateUrl = default(string);

        var reader = new Utf8JsonReader(_utf8.GetBytes(json));
        var prop = default(string);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                prop = reader.GetString();
            }
            else if (prop == "boundStatus" && reader.TokenType == JsonTokenType.String)
            {
                status = reader.GetString();
                haveStatus = true;
            }
            else if (prop == "active" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                active = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "connectFailed" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                connectFailed = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "disconnectFailed" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                disconnectFailed = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "selectedTarget" && reader.TokenType == JsonTokenType.String)
            {
                selected = reader.GetString();
            }
            else if (prop == "boundTarget" && reader.TokenType == JsonTokenType.String)
            {
                bound = reader.GetString();
            }
            else if (prop == "showNotifications" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                notify = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "updateAvailable" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                updateAvail = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "updateDownloaded" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                updateDownloaded = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "updateDownloading" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                updateDownloading = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "updateDownloadPercent" && reader.TokenType == JsonTokenType.Number)
            {
                updateDownloadPercent = reader.GetInt32();
            }
            else if (prop == "updateDownloadFailed" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                updateFailed = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "updateChecking" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                updateChecking = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "updateCheckFailed" && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                updateCheckFailed = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "updateVersion" && reader.TokenType == JsonTokenType.String)
            {
                updateVer = reader.GetString();
            }
            else if (prop == "updateUrl" && reader.TokenType == JsonTokenType.String)
            {
                updateUrl = reader.GetString();
            }
        }

        if (!haveStatus)
        {
            return false;
        }

        SnapshotSeen = true;
        // The agent reports "disconnecting" only for a user-initiated teardown, never for a liveness re-dial,
        // so it distinguishes a disconnect start from a reconnect that also shows as the transitioning state.
        disconnecting = string.Equals(status, "disconnecting", StringComparison.Ordinal);
        ShowNotifications = notify;
        UpdateAvailable = updateAvail;
        UpdateDownloaded = updateDownloaded;
        DownloadInProgress = updateDownloading;
        DownloadPercent = updateDownloadPercent;
        DownloadFailed = updateFailed;
        CheckInProgress = updateChecking;
        CheckFailed = updateCheckFailed;
        UpdateVersion = updateVer ?? string.Empty;
        HasUpdateUrl = !string.IsNullOrWhiteSpace(updateUrl);
        HasActiveProfile = !string.IsNullOrEmpty(selected) || !string.IsNullOrEmpty(bound);
        state = status switch
        {
            "connected" => active ? 2 : 1,
            "connecting" or "disconnecting" => 1,
            _ => active ? 1 : 0,
        };
        return true;
    }
}
