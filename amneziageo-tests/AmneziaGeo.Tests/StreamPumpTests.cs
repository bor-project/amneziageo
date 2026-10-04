using System.Buffers;
using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// One direction of a relayed stream: bytes go across, the counter hears of them, and a stream that waits holds
/// no buffer.
/// </summary>
public sealed class StreamPumpTests : IDisposable
{
    private const int BufferSize = 65536;
    private readonly List<Socket> _sockets = [];
    private readonly CancellationTokenSource _limit = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Bytes_GoAcrossAndAreCounted()
    {
        var (source, near) = Pair();
        var (far, sink) = Pair();
        var pool = new CountingPool();
        var counted = 0;
        var pump = StreamPump.RunAsync(near, far, BufferSize, 1024, bytes => Interlocked.Add(ref counted, bytes), pool,
            _limit.Token);
        var sent = new byte[300_000];
        Random.Shared.NextBytes(sent);

        await source.SendAsync(sent, SocketFlags.None, _limit.Token);
        var got = await ReadAsync(sink, sent.Length);
        source.Shutdown(SocketShutdown.Send);
        await pump.WaitAsync(TimeSpan.FromSeconds(10), _limit.Token);

        Assert.Equal(sent, got);
        Assert.Equal(sent.Length, counted);
        Assert.Equal(0, pool.Held);
    }

    [Fact]
    public async Task AStreamThatWaits_HoldsNoBuffer()
    {
        var (source, near) = Pair();
        var (far, sink) = Pair();
        var pool = new CountingPool();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_limit.Token);
        var pump = StreamPump.RunAsync(near, far, BufferSize, 1024, _ => { }, pool, stop.Token);

        await source.SendAsync(new byte[2000], SocketFlags.None, _limit.Token);
        await ReadAsync(sink, 2000);
        var held = await SettledAsync(pool);

        Assert.Equal(0, held);
        Assert.True(pool.Rented > 0);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, pool.Held);
    }

    [Fact]
    public async Task AStreamThatNeverSpoke_TakesNoBufferAtAll()
    {
        var (source, near) = Pair();
        var (far, _) = Pair();
        var pool = new CountingPool();
        var pump = StreamPump.RunAsync(near, far, BufferSize, 1024, _ => { }, pool, _limit.Token);

        await Task.Delay(200, _limit.Token);
        var rentedWhileSilent = pool.Rented;
        source.Shutdown(SocketShutdown.Send);
        await pump.WaitAsync(TimeSpan.FromSeconds(10), _limit.Token);

        Assert.Equal(0, rentedWhileSilent);
        Assert.Equal(0, pool.Held);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }

        _limit.Dispose();
    }

    // Two connected sockets on the loopback.
    private (Socket Near, Socket Far) Pair()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var near = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        near.Connect(listener.LocalEndPoint!);
        var far = listener.Accept();
        far.NoDelay = true;
        _sockets.Add(near);
        _sockets.Add(far);
        return (near, far);
    }

    private async Task<byte[]> ReadAsync(Socket socket, int size)
    {
        var data = new byte[size];
        var used = 0;
        while (used < size)
        {
            var read = await socket.ReceiveAsync(data.AsMemory(used), SocketFlags.None, _limit.Token);
            if (read <= 0)
            {
                break;
            }

            used += read;
        }

        return data[..used];
    }

    // What the pool has out once the pump has gone back to waiting.
    private async Task<int> SettledAsync(CountingPool pool)
    {
        for (var attempt = 0; attempt < 750 && pool.Held > 0; attempt++)
        {
            await Task.Delay(20, _limit.Token);
        }

        return pool.Held;
    }

    /// <summary>
    /// A pool that counts what is out of it.
    /// </summary>
    private sealed class CountingPool : ArrayPool<byte>
    {
        private int _held;
        private int _rented;

        /// <summary>
        /// Buffers taken and not yet given back.
        /// </summary>
        public int Held => Volatile.Read(ref _held);

        /// <summary>
        /// Buffers taken so far.
        /// </summary>
        public int Rented => Volatile.Read(ref _rented);

        /// <inheritdoc/>
        public override byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref _held);
            Interlocked.Increment(ref _rented);
            return new byte[minimumLength];
        }

        /// <inheritdoc/>
        public override void Return(byte[] array, bool clearArray = false)
        {
            Interlocked.Decrement(ref _held);
        }
    }
}
