using System.Net;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Moves the machine's lookups onto HTTPS when they stop reaching the name proxy on plain 53, and puts
/// everything back when the tunnel goes down.
/// </summary>
internal sealed class LocalDohGuard(string mode, IPAddress address, ILogger logger) : IDisposable
{
    // How long Windows is given to pick the HTTPS resolver up before the answer is asked for.
    private static readonly TimeSpan _settle = TimeSpan.FromSeconds(3);

    private readonly string _mode = LocalDohModes.Of(mode);
    private LocalDohServer? _server;
    private bool _registered;
    private bool _tried;

    /// <summary>
    /// What the system uses to reach this machine's resolver, empty while it uses plain 53.
    /// </summary>
    public string State { get; private set; } = string.Empty;

    /// <summary>
    /// Whether this mode puts the resolver on HTTPS before anything goes wrong.
    /// </summary>
    public bool Eager => _mode == LocalDohModes.On;

    /// <summary>
    /// Opens the resolver on HTTPS and tells Windows to ask it there; tells whether both are in place. One
    /// session gets one attempt, so a machine the takeover does not suit is not made to live through it twice.
    /// </summary>
    public bool Serve(Func<byte[], CancellationToken, Task<byte[]?>> answer)
    {
        if (_mode == LocalDohModes.Off || _tried)
        {
            return false;
        }

        _tried = true;
        var server = new LocalDohServer(address, logger);
        if (!server.Start(answer))
        {
            server.Dispose();
            return false;
        }

        _server = server;
        if (!EncryptedNames.Register(address, server.Template, logger))
        {
            Release();
            return false;
        }

        _registered = true;
        return true;
    }

    /// <summary>
    /// Keeps the takeover only if the system's own lookups come back answered by then.
    /// </summary>
    public async Task<bool> ConfirmAsync(Func<CancellationToken, Task<bool>> systemAnswers, CancellationToken ct)
    {
        await Task.Delay(_settle, ct).ConfigureAwait(false);
        if (await systemAnswers(ct).ConfigureAwait(false))
        {
            State = $"doh {address}";
            logger.LogInformation("the system's lookups reach the name proxy over HTTPS on {Address}, so rules by domain apply again", address);
            return true;
        }

        logger.LogWarning("the system did not take to asking {Address} over HTTPS either, so this machine stays on the resolvers it had", address);
        Release();
        return false;
    }

    /// <summary>
    /// Puts the machine back on plain 53 and drops the entry.
    /// </summary>
    public void Release()
    {
        if (_registered)
        {
            EncryptedNames.Unregister(address, logger);
            _registered = false;
        }

        _server?.Dispose();
        _server = null;
        State = string.Empty;
    }

    /// <summary>
    /// Releases the takeover at teardown.
    /// </summary>
    public void Dispose()
    {
        Release();
    }
}
