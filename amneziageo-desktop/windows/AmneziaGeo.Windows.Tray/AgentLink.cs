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

    private static readonly UTF8Encoding _utf8 = new(false);
    private static volatile StreamWriter? _writer;
    private static Action<int, bool>? _onState;
    private static Action<bool, string>? _onUpdate;

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
    /// The available update version, empty when none.
    /// </summary>
    public static volatile string UpdateVersion = string.Empty;

    /// <summary>
    /// Whether the build has an update URL configured; without one there is nothing to check against.
    /// </summary>
    public static volatile bool HasUpdateUrl;

    /// <summary>
    /// Starts the connection loop; <paramref name="onState"/> receives 0 (disconnected) / 1 (transitioning) /
    /// 2 (connected) plus whether the agent latched a connect failure; <paramref name="onUpdate"/> receives the
    /// update-available flag and version whenever they change. Both fire off a background thread.
    /// </summary>
    public static void Start(Action<int, bool> onState, Action<bool, string> onUpdate)
    {
        _onState = onState;
        _onUpdate = onUpdate;
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
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length > 0 && TryReadState(line, out var state, out var connectFailed))
                    {
                        _onState?.Invoke(state, connectFailed);
                        if (UpdateAvailable != prevUpdateAvail || !string.Equals(UpdateVersion, prevUpdateVer, StringComparison.Ordinal))
                        {
                            prevUpdateAvail = UpdateAvailable;
                            prevUpdateVer = UpdateVersion;
                            _onUpdate?.Invoke(UpdateAvailable, UpdateVersion);
                        }
                    }
                }
            }
            catch
            {
                // fall through to reconnect
            }

            _writer = null;
            HasActiveProfile = false;
            _onState?.Invoke(0, false);
            Thread.Sleep(2000);
        }
    }

    // Maps a snapshot line to the three-way tray state, matching ConnectionViewModel.ConnState, plus the agent's
    // latched connect-failure flag (set by FailConnect, cleared on any user connect/disconnect).
    private static bool TryReadState(string json, out int state, out bool connectFailed)
    {
        state = 0;
        connectFailed = false;
        var status = default(string);
        var active = true;
        var haveStatus = false;
        var selected = default(string);
        var bound = default(string);
        var notify = true;
        var updateAvail = false;
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
        ShowNotifications = notify;
        UpdateAvailable = updateAvail;
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
