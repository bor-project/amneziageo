using System.Text.Json.Nodes;
using AmneziaGeo.Cli;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The answer of a console command hides its secrets before it travels as a broadcast reply: a config text
/// keeps its lines without the keys, an import link keeps nothing but its scheme, and a JSON answer keeps its
/// shape with the credential, password and key values alone replaced. What carries no secret stays as it was.
/// </summary>
public sealed class CliSecretsTests
{
    private const string Config = """
        [Interface]
        PrivateKey = SGVsbG8gV29ybGQga2V5IG9mIHRoZSBjbGllbnQ=
        Address = 10.8.0.2/32

        [Peer]
        PublicKey = c29tZSBwdWJsaWMga2V5IG9mIHRoZSBzZXJ2ZXI=
        PresharedKey = cHJlc2hhcmVkIHNlY3JldCBvZiB0aGUgcGVlcg==
        Endpoint = vpn.example:51820
        AllowedIPs = 0.0.0.0/0
        """;

    [Fact]
    public void AConfigText_LosesItsKeysAndKeepsItsLines()
    {
        var masked = CliSecrets.Mask(Config);

        Assert.DoesNotContain("SGVsbG8", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("cHJlc2hhcmVk", masked, StringComparison.Ordinal);
        Assert.Contains("PrivateKey = ***", masked, StringComparison.Ordinal);
        Assert.Contains("PresharedKey = ***", masked, StringComparison.Ordinal);
        Assert.Contains("PublicKey = c29tZSBwdWJsaWMga2V5IG9mIHRoZSBzZXJ2ZXI=", masked, StringComparison.Ordinal);
        Assert.Contains("Endpoint = vpn.example:51820", masked, StringComparison.Ordinal);
        Assert.Contains("[Interface]", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportLink_KeepsNothingButItsScheme()
    {
        var masked = CliSecrets.Mask("vpn://AAAC0HiclVJdTyIxFP0rpq_iOB-ymkl8ADVG2CyDuBpgCOlM7wx1h07");

        Assert.Equal("vpn://***", masked);
    }

    [Fact]
    public void AnAppLink_KeepsNothingButItsScheme()
    {
        var masked = CliSecrets.Mask("amneziageo://import?url=aGVsbG8gd29ybGQgY29uZmlnIGluIHRoZSBsaW5r");

        Assert.Equal("amneziageo://***", masked);
    }

    [Fact]
    public void AJsonAnswer_KeepsItsShapeWithTheSecretsAloneHidden()
    {
        const string answer = """
            {
              "ok": true,
              "proxy": {
                "id": "edge",
                "transport": "websocket",
                "auth": { "scheme": "bearer", "credential": "c2VjcmV0LWNyZWRlbnRpYWw=" },
                "tls": { "serverName": "relay.example", "pin": "abc123" }
              },
              "proxyCredentials": "user:pass",
              "password": "hunter2",
              "servers": [ { "name": "one", "credential": "one-credential" }, { "name": "two" } ]
            }
            """;

        var masked = CliSecrets.Mask(answer);

        Assert.DoesNotContain("c2VjcmV0", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("one-credential", masked, StringComparison.Ordinal);
        var parsed = JsonNode.Parse(masked)!;
        Assert.Equal("***", parsed["proxy"]!["auth"]!["credential"]!.GetValue<string>());
        Assert.Equal("bearer", parsed["proxy"]!["auth"]!["scheme"]!.GetValue<string>());
        Assert.Equal("abc123", parsed["proxy"]!["tls"]!["pin"]!.GetValue<string>());
        Assert.Equal("***", parsed["proxyCredentials"]!.GetValue<string>());
        Assert.Equal("edge", parsed["proxy"]!["id"]!.GetValue<string>());
        Assert.Equal("***", parsed["servers"]![0]!["credential"]!.GetValue<string>());
        Assert.Equal("two", parsed["servers"]![1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void AnAnswerWithoutSecrets_StaysAsItWas()
    {
        const string answer = """
            usage: amneziageo config show <name>
            wrote /data/local/tmp/bundle.zip (mode 600: it holds private keys)
            """;

        Assert.Equal(answer, CliSecrets.Mask(answer));
    }

    [Fact]
    public void AConfigSection_IsNotTakenForJson()
    {
        var masked = CliSecrets.Mask("[Interface]\nPrivateKey = a2V5\n");

        Assert.Equal("[Interface]\nPrivateKey = ***\n", masked);
    }
}
