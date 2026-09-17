using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Geo;

/// <summary>
/// Asks a resolver over TCP, which a filter watching datagrams on port 53 lets through.
/// </summary>
public static class StreamDns
{
    private const int Port = 53;

    /// <summary>
    /// Sends one query as it stands and returns the answer as it came.
    /// </summary>
    public static async Task<byte[]> AskAsync(byte[] query, IPAddress server, TimeSpan timeout, Action<Socket>? bind = null, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        bind?.Invoke(socket);
        await socket.ConnectAsync(new IPEndPoint(server, Port), deadline.Token).ConfigureAwait(false);

        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await socket.SendAsync(framed, SocketFlags.None, deadline.Token).ConfigureAwait(false);

        var header = new byte[2];
        await ReadExactAsync(socket, header, deadline.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length == 0)
        {
            throw new IOException("the resolver framed an empty answer");
        }

        var answer = new byte[length];
        await ReadExactAsync(socket, answer, deadline.Token).ConfigureAwait(false);
        return answer;
    }

    // Fills the buffer or fails; a short read means the resolver hung up mid-answer.
    private static async Task ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, ct).ConfigureAwait(false);
            if (got <= 0)
            {
                throw new IOException("the resolver closed the connection before the answer was whole");
            }

            read += got;
        }
    }
}
