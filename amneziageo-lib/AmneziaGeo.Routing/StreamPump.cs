using System.Buffers;
using System.Net.Sockets;

namespace AmneziaGeo.Routing;

/// <summary>
/// Carries one direction of a stream between two sockets. A buffer is taken only while bytes are there to carry,
/// so a stream that waits holds none.
/// </summary>
public static class StreamPump
{
    /// <summary>
    /// Copies until the stream ends and tells the counter what was carried, in portions of at least the size given.
    /// </summary>
    public static async Task RunAsync(Socket from, Socket to, int bufferSize, int countEvery, Action<int> count,
        ArrayPool<byte> pool, CancellationToken ct)
    {
        var buffer = default(byte[]);
        var carried = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ready = from.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None, ct);
                if (!ready.IsCompleted && buffer is not null)
                {
                    pool.Return(buffer);
                    buffer = null;
                }

                await ready.ConfigureAwait(false);
                buffer ??= pool.Rent(bufferSize);
                var read = await from.ReceiveAsync(buffer.AsMemory(0, bufferSize), SocketFlags.None, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, ct).ConfigureAwait(false);
                carried += read;
                if (carried >= countEvery)
                {
                    count(carried);
                    carried = 0;
                }
            }
        }
        finally
        {
            if (carried > 0)
            {
                count(carried);
            }

            if (buffer is not null)
            {
                pool.Return(buffer);
            }
        }
    }
}
