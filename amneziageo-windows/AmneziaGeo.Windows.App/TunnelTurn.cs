using System.Diagnostics;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The turn of a tunnel among its sessions: one process at a time holds it, from bring-up until its routes, firewall
/// rules and DNS changes are taken down.
/// </summary>
internal sealed class TunnelTurn : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly FileStream? _file;

    /// <summary>
    /// ctor
    /// </summary>
    private TunnelTurn(FileStream? file, TimeSpan waited)
    {
        _file = file;
        Waited = waited;
    }

    /// <summary>
    /// Whether the session holds the turn.
    /// </summary>
    public bool Held => _file is not null;

    /// <summary>
    /// How long the session waited for the turn.
    /// </summary>
    public TimeSpan Waited { get; }

    /// <summary>
    /// Takes the turn kept in the file given, waiting up to the limit for the session that holds it.
    /// </summary>
    public static async Task<TunnelTurn> TakeAsync(string path, TimeSpan limit, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clock = Stopwatch.StartNew();
        var tries = 0;
        while (true)
        {
            try
            {
                var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new TunnelTurn(file, tries == 0 ? TimeSpan.Zero : clock.Elapsed);
            }
            catch (IOException) when (clock.Elapsed < limit)
            {
                tries++;
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return new TunnelTurn(null, clock.Elapsed);
            }
        }
    }

    /// <summary>
    /// Gives the turn up.
    /// </summary>
    public void Dispose()
    {
        _file?.Dispose();
    }
}
