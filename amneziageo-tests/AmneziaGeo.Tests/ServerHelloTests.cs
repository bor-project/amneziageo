using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The point a client opens on the server of its own configuration: the answer that proves the key, what the
/// server offers for the speed, what is kept of that for the window, and which of those offers a probe is
/// allowed to measure against. The keys never travel, so an answer both sides count the same is the whole of
/// the proof, and the window is answered out of what was asked before rather than over the network.
/// </summary>
public sealed class ServerHelloTests
{
    [Fact]
    public void TheAnswer_IsTheSameOnBothSidesOfTheKeys()
    {
        var client = Keys();
        var server = Keys();
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var mine = PeerProof.Answer(client.Private, server.Public, challenge);
        var theirs = PeerProof.Answer(server.Private, client.Public, challenge);

        Assert.Equal(theirs, mine);
    }

    [Fact]
    public void TheAnswer_IsBoundToTheChallengeItWasGivenFor()
    {
        var client = Keys();
        var server = Keys();

        Assert.NotEqual(
            PeerProof.Answer(client.Private, server.Public, "one"),
            PeerProof.Answer(client.Private, server.Public, "two"));
    }

    [Fact]
    public async Task AServerOfOurs_HandsOverWhereItMeasuresTheSpeed()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);

        var offer = await ServerHello.AskAsync(panel.Origin, client.Private, server.Public, true, CancellationToken.None);

        Assert.NotNull(offer);
        Assert.Equal(panel.Origin + "/api/speed/up?ticket=pass-1", offer.Up);
        Assert.Equal(104857600L, offer.Limit);
        Assert.True(offer.Inside);
        Assert.Equal(client.Public, panel.Proven);
    }

    [Fact]
    public async Task AKeyTheServerDoesNotKnow_IsOfferedNothing()
    {
        var server = Keys();
        using var panel = new Panel(server.Private, Keys().Public);

        var offer = await ServerHello.AskAsync(panel.Origin, Keys().Private, server.Public, true, CancellationToken.None);

        Assert.Null(offer);
    }

    [Fact]
    public async Task AnythingButAServerOfOurs_IsOfferedNothing()
    {
        var server = Keys();
        using var panel = new Panel(server.Private, Keys().Public) { Name = "someone-else" };

        var offer = await ServerHello.AskAsync(panel.Origin, Keys().Private, server.Public, false, CancellationToken.None);

        Assert.Null(offer);
    }

    [Fact]
    public void BeforeAnyServerIsAsked_TheWindowIsToldTheServiceStands()
    {
        var told = new ServerSpeed().Told("stand");

        Assert.False(told.Own);
        Assert.Equal(string.Empty, told.Against);
    }

    [Fact]
    public async Task WhatAServerAnswered_IsKeptForTheWindowToRead()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var speed = new ServerSpeed(panel.Port);

        await speed.WarmAsync([Target(client.Private, server.Public)], CancellationToken.None);

        var told = speed.Told("stand");
        Assert.True(told.Own);
        Assert.Equal("stand", told.Server);
        Assert.Equal("localhost:" + panel.Port.ToString(CultureInfo.InvariantCulture), told.Against);
    }

    [Fact]
    public async Task AskingTheSameServerAgainWhileItsAnswerStands_CostsNothing()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var speed = new ServerSpeed(panel.Port);

        await speed.WarmAsync([Target(client.Private, server.Public)], CancellationToken.None);
        var asked = panel.Asked;
        await speed.WarmAsync([Target(client.Private, server.Public)], CancellationToken.None);

        Assert.Equal(asked, panel.Asked);
    }

    [Fact]
    public async Task EveryRun_TakesAPassOfItsOwn()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var speed = new ServerSpeed(panel.Port);
        var target = Target(client.Private, server.Public);

        await speed.WarmAsync([target], CancellationToken.None);
        var first = await speed.TicketAsync(target, CancellationToken.None);
        var second = await speed.TicketAsync(target, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Up, second.Up);
    }

    [Fact]
    public async Task AServerKnownToOfferNothing_IsNotAskedAgainByARun()
    {
        var server = Keys();
        using var panel = new Panel(server.Private, Keys().Public);
        var speed = new ServerSpeed(panel.Port);
        var target = Target(Keys().Private, server.Public);

        await speed.WarmAsync([target], CancellationToken.None);
        var asked = panel.Asked;
        var offer = await speed.TicketAsync(target, CancellationToken.None);

        Assert.Null(offer);
        Assert.False(speed.Told("stand").Own);
        Assert.Equal(asked, panel.Asked);
    }

    [Fact]
    public void TheServiceInTheSettings_OutranksTheServerOffer()
    {
        var chosen = ServerSpeed.Upload("https://speed.example/__up", Offer(false), ProbePaths.Auto);

        Assert.Equal("https://speed.example/__up", chosen.Url);
        Assert.False(chosen.Own);
    }

    [Fact]
    public void WithNothingChosenAndNothingOffered_TheBuiltInServiceDecides()
    {
        var chosen = ServerSpeed.Upload(string.Empty, null, ProbePaths.Auto);

        Assert.Equal(string.Empty, chosen.Url);
        Assert.False(chosen.Own);
    }

    [Fact]
    public void AServerReachedInsideTheTunnel_IsNotMeasuredPastIt()
    {
        var inside = ServerSpeed.Upload(string.Empty, Offer(true), ProbePaths.Bypass);
        var through = ServerSpeed.Upload(string.Empty, Offer(true), ProbePaths.Tunnel);

        Assert.Equal(string.Empty, inside.Url);
        Assert.Equal("https://10.9.0.1:8443/api/speed/up?ticket=pass", through.Url);
        Assert.True(through.Own);
    }

    [Fact]
    public void AServerReachedAtItsEndpoint_IsMeasuredOnEitherPath()
    {
        var beside = ServerSpeed.Upload(string.Empty, Offer(false), ProbePaths.Bypass);

        Assert.True(beside.Own);
    }

    [Fact]
    public void WhatTheWindowIsTold_SurvivesTheAckItTravelsIn()
    {
        var told = new SpeedService(true, "home", "10.9.0.1:8443");

        var read = SpeedService.Parse(told.ToPayload());

        Assert.True(read.Own);
        Assert.Equal("home", read.Server);
        Assert.Equal("10.9.0.1:8443", read.Against);
    }

    [Fact]
    public void TheKeysOfAConfig_AreReadOutOfItsText()
    {
        var keys = Keys();
        var text = $"[Interface]\nPrivateKey = {keys.Private}\nAddress = 10.9.0.5/32\n\n[Peer]\nPublicKey = {keys.Public}\n";

        Assert.Equal(keys.Private, WgConfigEditor.GetPrivateKey(text));
        Assert.Equal(keys.Public, WgConfigEditor.GetPeerPublicKey(text));
    }

    // A configuration dialled at the host the panel of the tests answers on, with no tunnel up.
    private static SpeedTarget Target(string privateKey, string serverKey) => new(
        "stand",
        $"[Interface]\nPrivateKey = {privateKey}\nAddress = 10.9.0.5/32\n\n[Peer]\nPublicKey = {serverKey}\nEndpoint = localhost:51820\n",
        false);

    private static SpeedOffer Offer(bool inside) => new(
        "https://10.9.0.1:8443",
        "https://10.9.0.1:8443/api/speed/down?bytes=25000000&ticket=pass",
        "https://10.9.0.1:8443/api/speed/up?ticket=pass",
        104857600,
        DateTimeOffset.UtcNow.AddMinutes(5),
        inside);

    private static (string Private, string Public) Keys()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(Curve25519.KeySize));

        return (secret, Curve25519.PublicOf(secret));
    }

    // A panel of ours, as far as the point goes: it hands out a challenge, counts the answer from its own key,
    // and offers the addresses only to the peer it carries, with a fresh pass every time.
    private sealed class Panel : IDisposable
    {
        private const string Challenge = "kR2s8yPZ1q0mVb7uW5xT4cE6nA3dH9fLpQjXsYzKrMg=";

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly string _privateKey;
        private int _passes;
        private int _asked;

        /// <summary>
        /// ctor
        /// </summary>
        public Panel(string privateKey, string known)
        {
            _privateKey = privateKey;
            Known = known;
            Port = Free();
            Origin = "http://localhost:" + Port.ToString(CultureInfo.InvariantCulture);
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        /// <summary>
        /// Where it answers.
        /// </summary>
        public string Origin { get; }

        /// <summary>
        /// The port it answers on.
        /// </summary>
        public int Port { get; }

        /// <summary>
        /// The public key of the only peer it carries.
        /// </summary>
        public string Known { get; }

        /// <summary>
        /// The word it answers under.
        /// </summary>
        public string Name { get; init; } = ServerHello.ServerName;

        /// <summary>
        /// The public key of the peer whose answer held, empty while none has.
        /// </summary>
        public string Proven { get; private set; } = string.Empty;

        /// <summary>
        /// How many requests it has been handed.
        /// </summary>
        public int Asked => Volatile.Read(ref _asked);

        /// <inheritdoc/>
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = default(HttpListenerContext);
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                await AnswerAsync(context).ConfigureAwait(false);
            }
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref _asked);
            var body = context.Request.HttpMethod == "POST"
                ? await new StreamReader(context.Request.InputStream).ReadToEndAsync().ConfigureAwait(false)
                : string.Empty;
            var answer = body.Length == 0 ? Greeting() : Features(body, context.Response);

            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(answer);
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }

        private string Greeting() =>
            JsonSerializer.Serialize(new { server = Name, version = "1.0.3.0", challenge = Challenge });

        private string Features(string body, HttpListenerResponse response)
        {
            using var asked = JsonDocument.Parse(body);
            var key = asked.RootElement.GetProperty("key").GetString() ?? string.Empty;
            var proof = asked.RootElement.GetProperty("proof").GetString() ?? string.Empty;
            if (!string.Equals(key, Known, StringComparison.Ordinal))
            {
                response.StatusCode = 403;

                return JsonSerializer.Serialize(new { error = "unknown-peer" });
            }

            if (PeerProof.Answer(_privateKey, key, Challenge) != proof)
            {
                response.StatusCode = 403;

                return JsonSerializer.Serialize(new { error = "bad-proof" });
            }

            Proven = key;
            var pass = "pass-" + Interlocked.Increment(ref _passes).ToString(CultureInfo.InvariantCulture);

            return JsonSerializer.Serialize(new
            {
                server = Name,
                version = "1.0.3.0",
                client = "milena",
                features = new[] { ServerHello.SpeedFeature },
                speed = new
                {
                    down = Origin + "/api/speed/down?bytes=25000000&ticket=" + pass,
                    up = Origin + "/api/speed/up?ticket=" + pass,
                    limit = 104857600,
                    expires = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
                },
            });
        }

        // A port nothing else holds right now.
        private static int Free()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            return port;
        }
    }
}
