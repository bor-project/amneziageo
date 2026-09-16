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
/// The hello of a server of ours: the proofs of both sides, the dictionary of features, and when it is asked again.
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
    public void TheCountersign_IsTheSameOnBothSidesAndBoundToTheNonceAndTheBody()
    {
        var client = Keys();
        var server = Keys();
        var body = "{\"features\":{}}"u8.ToArray();

        var theirs = PeerProof.Countersign(server.Private, client.Public, "nonce", body);

        Assert.True(PeerProof.Countersigns(client.Private, server.Public, "nonce", body, theirs));
        Assert.False(PeerProof.Countersigns(client.Private, server.Public, "other", body, theirs));
        Assert.False(PeerProof.Countersigns(client.Private, server.Public, "nonce", "{}"u8, theirs));
        Assert.False(PeerProof.Countersigns(client.Private, Keys().Public, "nonce", body, theirs));
        Assert.False(PeerProof.Countersigns(client.Private, server.Public, "nonce", body, null));
    }

    [Fact]
    public async Task AServerOfOurs_HandsOverItsFeaturesByName()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);

        var reply = await ServerHello.AskAsync(panel.Origin, client.Private, server.Public, true, CancellationToken.None);

        Assert.NotNull(reply.Offer);
        Assert.True(reply.Offer.Ours);
        Assert.True(reply.Offer.Inside);
        Assert.Equal("milena", reply.Offer.Client);
        Assert.Equal(["future", "speed", "subscription"], reply.Offer.Features.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(client.Public, panel.Proven);
    }

    [Fact]
    public async Task TheArgumentsOfAKnownFeature_AreReadAndTheUnknownOnesPassedOver()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);

        var reply = await ServerHello.AskAsync(panel.Origin, client.Private, server.Public, true, CancellationToken.None);
        var speed = SpeedArgs.Of(reply.Offer);

        Assert.NotNull(speed);
        Assert.Equal(panel.Origin + "/api/speed/up?ticket=pass-1", speed.Up);
        Assert.Equal(104857600L, speed.Limit);
    }

    [Fact]
    public async Task AFeatureWithBrokenArguments_IsNotOffered()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public) { Broken = true };

        var reply = await ServerHello.AskAsync(panel.Origin, client.Private, server.Public, true, CancellationToken.None);

        Assert.NotNull(reply.Offer);
        Assert.Null(SpeedArgs.Of(reply.Offer));
    }

    [Fact]
    public async Task AnAnswerTheServerKeyDidNotCountersign_IsTakenAsNoServerOfOurs()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public) { Signer = Keys().Private };

        var reply = await ServerHello.AskAsync(panel.Origin, client.Private, server.Public, true, CancellationToken.None);

        Assert.Null(reply.Offer);
        Assert.True(reply.Heard);
    }

    [Fact]
    public async Task AnAnswerChangedOnTheWay_IsTakenAsNoServerOfOurs()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public) { Tamper = true };

        var reply = await ServerHello.AskAsync(panel.Origin, client.Private, server.Public, true, CancellationToken.None);

        Assert.Null(reply.Offer);
    }

    [Fact]
    public async Task AKeyTheServerDoesNotKnow_IsOfferedNothing()
    {
        var server = Keys();
        using var panel = new Panel(server.Private, Keys().Public);

        var reply = await ServerHello.AskAsync(panel.Origin, Keys().Private, server.Public, true, CancellationToken.None);

        Assert.Null(reply.Offer);
    }

    [Fact]
    public async Task AnythingButAServerOfOurs_IsOfferedNothing()
    {
        var server = Keys();
        using var panel = new Panel(server.Private, Keys().Public) { Name = "someone-else" };

        var reply = await ServerHello.AskAsync(panel.Origin, Keys().Private, server.Public, false, CancellationToken.None);

        Assert.Null(reply.Offer);
    }

    [Fact]
    public void BeforeAnyServerIsAsked_TheWindowIsToldNoServerOfOurs()
    {
        var offer = new ServerOffers(TimeSpan.Zero).Offer("stand");

        Assert.False(offer.Ours);
        Assert.Null(SpeedArgs.Of(offer));
    }

    [Fact]
    public async Task WhatAServerAnswered_IsKeptForTheWindowToRead()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var offers = new ServerOffers(TimeSpan.Zero);

        await offers.WarmAsync([Target(client.Private, server.Public, panel.Port)], CancellationToken.None);

        var offer = offers.Offer("stand");
        Assert.NotNull(SpeedArgs.Of(offer));
        Assert.Equal("stand", offer.Config);
        Assert.Equal("127.0.0.1:" + panel.Port.ToString(CultureInfo.InvariantCulture), offer.Authority());
    }

    [Fact]
    public async Task WithoutAReconnect_TheServerIsNotAskedAgain()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var offers = new ServerOffers(TimeSpan.Zero);

        await offers.WarmAsync([Target(client.Private, server.Public, panel.Port)], CancellationToken.None);
        var asked = panel.Asked;
        await offers.WarmAsync([Target(client.Private, server.Public, panel.Port)], CancellationToken.None);
        await offers.WarmAsync([Target(client.Private, server.Public, panel.Port) with { Connected = false }], CancellationToken.None);

        Assert.Equal(asked, panel.Asked);
    }

    [Fact]
    public async Task AfterAReconnect_TheServerIsAskedAgain()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var offers = new ServerOffers(TimeSpan.Zero);

        await offers.WarmAsync([Target(client.Private, server.Public, panel.Port)], CancellationToken.None);
        var asked = panel.Asked;
        Assert.False(offers.Observe("stand", true));
        offers.Observe("stand", false);
        Assert.True(offers.Observe("stand", true));
        await offers.WarmAsync([Target(client.Private, server.Public, panel.Port)], CancellationToken.None);

        Assert.True(panel.Asked > asked);
    }

    [Fact]
    public async Task AConfigWithoutAnUpTunnel_IsNotAsked()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var offers = new ServerOffers(TimeSpan.Zero);

        await offers.WarmAsync([Target(client.Private, server.Public, panel.Port) with { Connected = false }], CancellationToken.None);

        Assert.Equal(0, panel.Asked);
        Assert.False(offers.Offer("stand").Ours);
    }

    [Fact]
    public async Task TheApiPortOfTheSettings_OutranksThePortOfTheEndpoint()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var offers = new ServerOffers(TimeSpan.Zero);

        await offers.WarmAsync([Target(client.Private, server.Public, 9) with { ApiPort = panel.Port }], CancellationToken.None);

        Assert.True(offers.Offer("stand").Ours);
        Assert.Equal(panel.Port, new Uri(offers.Offer("stand").Origin).Port);
    }

    [Fact]
    public async Task AChangedConfig_IsAskedAgain()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var offers = new ServerOffers(TimeSpan.Zero);

        await offers.WarmAsync([Target(client.Private, server.Public, panel.Port)], CancellationToken.None);
        var asked = panel.Asked;
        var changed = Target(client.Private, server.Public, panel.Port) with { Text = Target(client.Private, server.Public, panel.Port).Text + "MTU = 1280\n" };
        await offers.WarmAsync([changed], CancellationToken.None);

        Assert.True(panel.Asked > asked);
    }

    [Fact]
    public async Task ARun_TakesTheKeptPassWhileItStands()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var offers = new ServerOffers(TimeSpan.Zero);
        var target = Target(client.Private, server.Public, panel.Port);

        await offers.WarmAsync([target], CancellationToken.None);
        var asked = panel.Asked;
        var first = SpeedArgs.Of(await offers.SpeedAsync(target, CancellationToken.None));
        var second = SpeedArgs.Of(await offers.SpeedAsync(target, CancellationToken.None));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Up, second.Up);
        Assert.Equal(asked, panel.Asked);
    }

    [Fact]
    public async Task ARun_TakesAFreshPassOnceTheKeptOneIsAboutToRunOut()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public) { PassLife = TimeSpan.FromSeconds(30) };
        var offers = new ServerOffers(TimeSpan.Zero);
        var target = Target(client.Private, server.Public, panel.Port);

        await offers.WarmAsync([target], CancellationToken.None);
        var kept = SpeedArgs.Of(offers.Offer("stand"));
        var run = SpeedArgs.Of(await offers.SpeedAsync(target, CancellationToken.None));

        Assert.NotNull(kept);
        Assert.NotNull(run);
        Assert.NotEqual(kept.Up, run.Up);
    }

    [Fact]
    public async Task TheServer_IsAskedWhereTheTextNamesIt()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var offers = new ServerOffers(TimeSpan.Zero);

        await offers.WarmAsync([Named(client.Private, server.Public, "127.0.0.1:" + panel.Port.ToString(CultureInfo.InvariantCulture))], CancellationToken.None);

        Assert.True(offers.Offer("stand").Ours);
        Assert.Equal("127.0.0.1:" + panel.Port.ToString(CultureInfo.InvariantCulture), offers.Offer("stand").Authority());
    }

    [Fact]
    public async Task TheApiPortOfTheSettings_OutranksThePortTheTextNames()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var offers = new ServerOffers(TimeSpan.Zero);

        await offers.WarmAsync([Named(client.Private, server.Public, "127.0.0.1:9") with { ApiPort = panel.Port }], CancellationToken.None);

        Assert.True(offers.Offer("stand").Ours);
    }

    [Fact]
    public async Task ASessionTheTunnelNames_OutlivesTheProcessThatAsked()
    {
        var client = Keys();
        var server = Keys();
        using var panel = new Panel(server.Private, client.Public);
        var file = Path.Combine(Path.GetTempPath(), "offers-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var target = Target(client.Private, server.Public, panel.Port) with { Session = "1757750000000" };
            await new ServerOffers(TimeSpan.Zero, file).WarmAsync([target], CancellationToken.None);
            var asked = panel.Asked;

            var again = new ServerOffers(TimeSpan.Zero, file);
            Assert.True(again.Offer("stand").Ours);
            Assert.False(again.Observe("stand", true, target.Session));
            await again.WarmAsync([target], CancellationToken.None);
            Assert.Equal(asked, panel.Asked);

            Assert.True(again.Observe("stand", true, "1757750099000"));
            await again.WarmAsync([target with { Session = "1757750099000" }], CancellationToken.None);
            Assert.True(panel.Asked > asked);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TheApiPoints_AreReadOutOfTheCommentOfTheText()
    {
        var text = "[Interface]\n# AmneziaGeo Api = 10.9.0.1:51820, [fd00::1]:9443, host:1, 10.9.0.2:70000\nAddress = 10.9.0.5/32\n\n[Peer]\nEndpoint = 192.0.2.1:51821\n";

        Assert.Equal([("10.9.0.1", 51820), ("fd00::1", 9443)], WgConfigEditor.GetApiPoints(text));
        Assert.Empty(WgConfigEditor.GetApiPoints("[Peer]\nEndpoint = 192.0.2.1:51821\n"));
        Assert.Equal(51820, ServerOffers.DefaultPort(text));
        Assert.Equal(51821, ServerOffers.DefaultPort("[Peer]\nEndpoint = 192.0.2.1:51821\n"));
        Assert.Equal(0, ServerOffers.DefaultPort(string.Empty));
    }

    [Fact]
    public async Task AServerKnownToOfferNothing_IsNotAskedAgainByARun()
    {
        var server = Keys();
        using var panel = new Panel(server.Private, Keys().Public);
        var offers = new ServerOffers(TimeSpan.Zero);
        var target = Target(Keys().Private, server.Public, panel.Port);

        await offers.WarmAsync([target], CancellationToken.None);
        var asked = panel.Asked;
        var offer = await offers.SpeedAsync(target, CancellationToken.None);

        Assert.Null(offer);
        Assert.False(offers.Offer("stand").Ours);
        Assert.Equal(asked, panel.Asked);
    }

    [Fact]
    public void TheServiceInTheSettings_OutranksTheServerOffer()
    {
        var chosen = ServerOffers.Upload("https://speed.example/__up", Offer(false), ProbePaths.Auto);

        Assert.Equal("https://speed.example/__up", chosen.Url);
        Assert.False(chosen.Own);
    }

    [Fact]
    public void WithNothingChosenAndNothingOffered_TheBuiltInServiceDecides()
    {
        var chosen = ServerOffers.Upload(string.Empty, null, ProbePaths.Auto);

        Assert.Equal(string.Empty, chosen.Url);
        Assert.False(chosen.Own);
    }

    [Fact]
    public void AServerReachedInsideTheTunnel_IsNotMeasuredPastIt()
    {
        var inside = ServerOffers.Upload(string.Empty, Offer(true), ProbePaths.Bypass);
        var through = ServerOffers.Upload(string.Empty, Offer(true), ProbePaths.Tunnel);

        Assert.Equal(string.Empty, inside.Url);
        Assert.Equal("https://10.9.0.1:8443/api/speed/up?ticket=pass", through.Url);
        Assert.True(through.Own);
    }

    [Fact]
    public void AServerReachedAtItsEndpoint_IsMeasuredOnEitherPath()
    {
        var beside = ServerOffers.Upload(string.Empty, Offer(false), ProbePaths.Bypass);

        Assert.True(beside.Own);
    }

    [Fact]
    public void ADownloadLeg_PullsFromTheServerThatMeasuresItself()
    {
        const string down = "https://10.9.0.1:8443/api/speed/down?bytes=25000000&ticket=pass";

        Assert.Equal(down, ServerOffers.Download(Offer(true), false));
        Assert.Equal(string.Empty, ServerOffers.Download(Offer(true), true));
        Assert.Equal(down, ServerOffers.Download(Offer(false), true));
    }

    [Fact]
    public void ADownloadLegWithNothingOffered_IsLeftToTheBuiltInService()
    {
        Assert.Equal(string.Empty, ServerOffers.Download(null, false));
        Assert.Equal(string.Empty, ServerOffers.Download(ServerOffer.None, false));
    }

    [Fact]
    public void WhatTheWindowIsTold_SurvivesTheAckItTravelsIn()
    {
        var read = ServerOffer.Parse(Offer(true).ToPayload());

        Assert.True(read.Ours);
        Assert.Equal("home", read.Config);
        Assert.Equal("10.9.0.1:8443", read.Authority());
        Assert.Equal("https://10.9.0.1:8443/api/speed/up?ticket=pass", SpeedArgs.Of(read)?.Up);
    }

    [Fact]
    public void AnAckThatIsNotAnOffer_IsReadAsNoServerOfOurs()
    {
        Assert.False(ServerOffer.Parse("not json").Ours);
        Assert.False(ServerOffer.Parse("{}").Ours);
    }

    [Fact]
    public void TheKeysOfAConfig_AreReadOutOfItsText()
    {
        var keys = Keys();
        var text = $"[Interface]\nPrivateKey = {keys.Private}\nAddress = 10.9.0.5/32\n\n[Peer]\nPublicKey = {keys.Public}\n";

        Assert.Equal(keys.Private, WgConfigEditor.GetPrivateKey(text));
        Assert.Equal(keys.Public, WgConfigEditor.GetPeerPublicKey(text));
    }

    // A configuration whose up tunnel reaches the panel of the tests at 127.0.0.1.
    private static OfferTarget Target(string privateKey, string serverKey, int endpointPort) => new(
        "stand",
        $"[Interface]\nPrivateKey = {privateKey}\nAddress = 127.0.0.5/24\n\n[Peer]\nPublicKey = {serverKey}\nEndpoint = 192.0.2.1:{endpointPort.ToString(CultureInfo.InvariantCulture)}\n",
        true);

    // A configuration whose text names the API of the server away from the first host of its subnet.
    private static OfferTarget Named(string privateKey, string serverKey, string point) => new(
        "stand",
        $"[Interface]\nPrivateKey = {privateKey}\nAddress = 10.99.0.5/24\n# AmneziaGeo Api = {point}\n\n[Peer]\nPublicKey = {serverKey}\nEndpoint = 192.0.2.1:9\n",
        true);

    private static ServerOffer Offer(bool inside)
    {
        using var json = JsonDocument.Parse(
            "{\"down\":\"https://10.9.0.1:8443/api/speed/down?bytes=25000000&ticket=pass\","
            + "\"up\":\"https://10.9.0.1:8443/api/speed/up?ticket=pass\",\"limit\":104857600}");

        return new ServerOffer(
            "home",
            "https://10.9.0.1:8443",
            inside,
            "1.0.3.0",
            "milena",
            new Dictionary<string, JsonElement> { [SpeedArgs.Name] = json.RootElement.Clone() });
    }

    private static (string Private, string Public) Keys()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(Curve25519.KeySize));

        return (secret, Curve25519.PublicOf(secret));
    }

    // A panel of ours as far as the hello goes.
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
            Signer = privateKey;
            Known = known;
            Port = Free();
            Origin = "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
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
        /// The private key it countersigns with.
        /// </summary>
        public string Signer { get; init; }

        /// <summary>
        /// Whether the speed arguments lack the upload address.
        /// </summary>
        public bool Broken { get; init; }

        /// <summary>
        /// Whether the body changes after it is countersigned.
        /// </summary>
        public bool Tamper { get; init; }

        /// <summary>
        /// How long a pass it hands out stands.
        /// </summary>
        public TimeSpan PassLife { get; init; } = TimeSpan.FromMinutes(5);

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
            var answer = body.Length == 0 ? Encoding.UTF8.GetBytes(Greeting()) : Features(body, context.Response);

            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(answer).ConfigureAwait(false);
            context.Response.Close();
        }

        private string Greeting() =>
            JsonSerializer.Serialize(new { server = Name, version = "1.0.3.0", challenge = Challenge });

        private byte[] Features(string body, HttpListenerResponse response)
        {
            using var asked = JsonDocument.Parse(body);
            var key = asked.RootElement.GetProperty("key").GetString() ?? string.Empty;
            var nonce = asked.RootElement.GetProperty("nonce").GetString() ?? string.Empty;
            var proof = asked.RootElement.GetProperty("proof").GetString() ?? string.Empty;
            if (!string.Equals(key, Known, StringComparison.Ordinal))
            {
                response.StatusCode = 403;

                return JsonSerializer.SerializeToUtf8Bytes(new { error = "unknown-peer" });
            }

            if (PeerProof.Answer(_privateKey, key, Challenge) != proof)
            {
                response.StatusCode = 403;

                return JsonSerializer.SerializeToUtf8Bytes(new { error = "bad-proof" });
            }

            Proven = key;
            var pass = "pass-" + Interlocked.Increment(ref _passes).ToString(CultureInfo.InvariantCulture);
            var answer = JsonSerializer.SerializeToUtf8Bytes(new
            {
                server = Name,
                version = "1.0.3.0",
                client = "milena",
                features = new Dictionary<string, object>
                {
                    ["speed"] = Broken
                        ? (object)new { down = Origin + "/api/speed/down?ticket=" + pass }
                        : new
                        {
                            down = Origin + "/api/speed/down?bytes=25000000&ticket=" + pass,
                            up = Origin + "/api/speed/up?ticket=" + pass,
                            limit = 104857600,
                            expires = DateTimeOffset.UtcNow.Add(PassLife).ToString("O"),
                        },
                    ["subscription"] = new { url = "https://localhost:2096/sub/one", updateHours = 12 },
                    ["future"] = new { anything = true },
                },
            });
            response.Headers[ServerHello.ProofHeader] = PeerProof.Countersign(Signer, key, nonce, answer);

            return Tamper ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(answer).Replace("milena", "mallory", StringComparison.Ordinal)) : answer;
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
