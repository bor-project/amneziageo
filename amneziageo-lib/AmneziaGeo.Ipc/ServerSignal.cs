using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Where a device takes the signal of its server to disconnect: its addresses inside the tunnel, the port, the
/// addresses the signal comes from and the keys it is sealed under.
/// </summary>
/// <param name="At">The addresses of the device inside the tunnel.</param>
/// <param name="Port">The TCP port the signal is taken on.</param>
/// <param name="From">The addresses of the server the signal comes from.</param>
/// <param name="PrivateKey">The private key of the config.</param>
/// <param name="ServerKey">The public key of the server.</param>
public sealed record SignalPlace(IReadOnlyList<IPAddress> At, int Port, IReadOnlyList<IPAddress> From, string PrivateKey, string ServerKey)
{
    /// <summary>
    /// The routes that lead back to where the signal comes from, one address each.
    /// </summary>
    public IReadOnlyList<string> Routes =>
        [.. From.Select(address => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"{address}/128" : $"{address}/32")];

    /// <summary>
    /// Returns the addresses the signal comes from as text.
    /// </summary>
    public IReadOnlyList<string> Sources => [.. From.Select(address => address.ToString())];

    /// <summary>
    /// Returns the addresses of the device the signal is taken at as text.
    /// </summary>
    public IReadOnlyList<string> Hosts => [.. At.Select(address => address.ToString())];
}

/// <summary>
/// Takes the signal a server of ours sends to take the tunnel down.
/// </summary>
public static class ServerSignal
{
    /// <summary>
    /// The word the signal carries.
    /// </summary>
    public const string Word = "disconnect";

    /// <summary>
    /// How long one caller is given to say the signal.
    /// </summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private const int LineLimit = 1024;

    private static readonly TimeSpan _bindRetry = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Listens at every address of the place until cancelled and runs the action each time a signal that holds was
    /// taken and answered.
    /// </summary>
    public static Task ListenAsync(SignalPlace place, Func<Task> down, Action<string, Exception?>? note, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(down);

        return Task.WhenAll(place.At.Select(address => ListenAtAsync(address, place, down, note, ct)));
    }

    /// <summary>
    /// Runs the exchange over one connection: hands out a nonce, opens the signal sealed under it and answers that it
    /// was taken. Returns whether a signal that holds was taken.
    /// </summary>
    public static async Task<bool> TakeAsync(Stream stream, SignalPlace place, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(place);

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(ServiceToken.NonceBytes));
        await WriteAsync(stream, Greeting(nonce), ct).ConfigureAwait(false);
        if (await LineAsync(stream, ct).ConfigureAwait(false) is not { } line || Sealed(line) is not { } said)
        {
            return false;
        }

        var body = ServiceToken.Open(place.PrivateKey, place.ServerKey, nonce, said.Iv, said.Data, ServiceToken.SignalContext);
        if (body is null || !string.Equals(Signal(body), Word, StringComparison.Ordinal))
        {
            return false;
        }

        var answer = ServiceToken.Seal(place.PrivateKey, place.ServerKey, nonce, Taken(), ServiceToken.SignalContext);
        await WriteAsync(stream, Sealed(answer.Iv, answer.Data), ct).ConfigureAwait(false);

        return true;
    }

    // Listens at one address; an address the machine does not carry yet is waited for.
    private static async Task ListenAtAsync(IPAddress address, SignalPlace place, Func<Task> down, Action<string, Exception?>? note, CancellationToken ct)
    {
        if (await BindAsync(address, place.Port, note, ct).ConfigureAwait(false) is not { } listener)
        {
            return;
        }

        using (listener)
        {
            note?.Invoke($"the signal of the server to disconnect is taken at {Point(address, place.Port)} from {string.Join(", ", place.Sources)}", null);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var caller = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => ServeAsync(caller, place, down, note, ct), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    note?.Invoke($"the signal of the server to disconnect is no longer taken at {Point(address, place.Port)}", ex);
                    return;
                }
            }
        }
    }

    // Binds the listener, trying again while the address or the port is not there to take.
    private static async Task<TcpListener?> BindAsync(IPAddress address, int port, Action<string, Exception?>? note, CancellationToken ct)
    {
        var told = SocketError.Success;
        while (!ct.IsCancellationRequested)
        {
            var listener = new TcpListener(address, port);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    listener.ExclusiveAddressUse = true;
                }

                listener.Start();

                return listener;
            }
            catch (SocketException ex)
            {
                listener.Dispose();
                if (ex.SocketErrorCode != told)
                {
                    told = ex.SocketErrorCode;
                    note?.Invoke($"the signal of the server to disconnect cannot be taken at {Point(address, port)} yet ({ex.SocketErrorCode}); trying again", null);
                }
            }

            try
            {
                await Task.Delay(_bindRetry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    // Hears one caller out; the tunnel goes down after the answer has left.
    private static async Task ServeAsync(TcpClient caller, SignalPlace place, Func<Task> down, Action<string, Exception?>? note, CancellationToken ct)
    {
        var taken = false;
        using (caller)
        {
            var remote = (caller.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var from = remote is { IsIPv4MappedToIPv6: true } ? remote.MapToIPv4() : remote;
            if (from is null || !place.From.Any(one => one.Equals(from)))
            {
                note?.Invoke($"a caller from {from} to the port of the signal to disconnect was turned away: the signal comes from the server alone", null);
                return;
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Patience);
            try
            {
                var stream = caller.GetStream();
                taken = await TakeAsync(stream, place, limit.Token).ConfigureAwait(false);
                if (taken)
                {
                    caller.Client.Shutdown(SocketShutdown.Send);
                    await stream.CopyToAsync(Stream.Null, limit.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }

            if (!taken)
            {
                note?.Invoke($"what {from} said on the port of the signal to disconnect is not a signal of the server", null);
                return;
            }
        }

        note?.Invoke("the server asked this device to disconnect", null);
        try
        {
            await down().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            note?.Invoke("the tunnel could not be taken down on the signal of the server", ex);
        }
    }

    // Reads one line, or null when the stream ends or the line outgrows its limit.
    private static async Task<byte[]?> LineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[LineLimit];
        var held = 0;
        while (held < LineLimit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(held), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var end = Array.IndexOf(buffer, (byte)'\n', held, read);
            if (end >= 0)
            {
                return buffer[..end];
            }

            held += read;
        }

        return null;
    }

    private static async Task WriteAsync(Stream stream, byte[] line, CancellationToken ct)
    {
        await stream.WriteAsync(line, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static byte[] Greeting(string nonce) => Line(writer => writer.WriteString("nonce", nonce));

    private static byte[] Sealed(string iv, string data) => Line(writer =>
    {
        writer.WriteString("iv", iv);
        writer.WriteString("data", data);
    });

    private static byte[] Taken()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("signal", Word);
            writer.WriteBoolean("taken", true);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // Renders one object as a line.
    private static byte[] Line(Action<Utf8JsonWriter> fields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            fields(writer);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');

        return buffer.ToArray();
    }

    // Reads the sealed line of the server.
    private static (string Iv, string Data)? Sealed(byte[] line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;

            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("iv", out var iv)
                && root.TryGetProperty("data", out var data)
                && iv.ValueKind == JsonValueKind.String
                && data.ValueKind == JsonValueKind.String
                    ? (iv.GetString()!, data.GetString()!)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Reads the word of an opened signal.
    private static string? Signal(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("signal", out var word)
                && word.ValueKind == JsonValueKind.String
                    ? word.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Point(IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? string.Create(CultureInfo.InvariantCulture, $"[{address}]:{port}")
            : string.Create(CultureInfo.InvariantCulture, $"{address}:{port}");
}
