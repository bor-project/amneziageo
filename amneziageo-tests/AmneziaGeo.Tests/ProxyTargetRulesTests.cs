using System.Text.Json;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class ProxyTargetRulesTests
{
    [Theory]
    [InlineData("VPN.Example.NET.", "vpn.example.net")]
    [InlineData("[2001:0db8::1]", "2001:db8::1")]
    [InlineData("::ffff:192.0.2.8", "192.0.2.8")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("BÜCHER.example", "xn--bcher-kva.example")]
    public void HostsHaveOneCanonicalRepresentation(string input, string expected)
    {
        Assert.Equal(expected, ProxyTargetRules.NormalizeHost(input));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.example.net")]
    [InlineData("192.0.2.0/24")]
    [InlineData("fe80::1%eth0")]
    [InlineData("https://example.net")]
    [InlineData("user@example.net")]
    [InlineData("bad host")]
    [InlineData("[vpn.example]")]
    [InlineData("bad_host.example")]
    [InlineData("vpn.example..")]
    public void HostPatternsAndUrlsAreNotDestinations(string host)
    {
        Assert.Null(ProxyTargetRules.NormalizeHost(host));
    }

    [Fact]
    public void PoliciesSeparatePortsAndHosts()
    {
        var policy = new ProxyTargets(true, [new("VPN.Example.NET.", 51820), new("192.0.2.8", 443)]);
        Assert.True(ProxyTargetRules.Allows(policy, "vpn.example.net", 51820));
        Assert.False(ProxyTargetRules.Allows(policy, "vpn.example.net", 51821));
        Assert.False(ProxyTargetRules.Allows(policy, "other.example.net", 51820));
        Assert.True(ProxyTargetRules.Allows(policy, "::ffff:192.0.2.8", 443));
        Assert.False(ProxyTargetRules.Allows(policy, "192.0.2.9", 443));
    }

    [Fact]
    public void MissingPolicyPreservesUnrestrictedConnections()
    {
        Assert.True(ProxyTargetRules.Allows(null, "external.example.net", 443));
        Assert.True(ProxyTargetRules.Allows(null, "vpn_node.example.net", 443));
        Assert.True(ProxyTargetRules.Allows(new ProxyTargets(false, []), "external.example.net", 443));
        Assert.False(ProxyTargetRules.Allows(new ProxyTargets(true, []), "external.example.net", 443));
        Assert.False(ProxyTargetRules.Allows(new ProxyTargets(false, null!), "external.example.net", 443));
    }

    [Fact]
    public void NormalizationDeduplicatesBeforeTheDistinctLimit()
    {
        var duplicates = Enumerable.Repeat(new ProxyDestination("VPN.Example.NET.", 443), 65).ToArray();
        var normalized = ProxyTargetRules.Normalize(new ProxyTargets(true, duplicates));
        Assert.Equal([new ProxyDestination("vpn.example.net", 443)], normalized!.Destinations);
        var distinct = Enumerable.Range(1, 65).Select(port => new ProxyDestination("vpn.example.net", port)).ToArray();
        Assert.False(ProxyTargetRules.Valid(new ProxyTargets(true, distinct)));
    }

    [Fact]
    public void RowsRoundTripIpv6AndMultiplePorts()
    {
        var policy = ProxyTargetRules.Parse("[2001:db8::1]:443\nvpn.example.net:51820\nvpn.example.net:51821", true);
        var rows = string.Join('\n', policy.Destinations.Select(ProxyTargetRules.Format));
        Assert.True(ProxyTargetRules.Same(policy, ProxyTargetRules.Parse(rows, true)));
        Assert.Throws<ArgumentException>(() => ProxyTargetRules.Parse("2001:db8::1:443", true));
        Assert.Throws<ArgumentException>(() => ProxyTargetRules.Parse("vpn.example.net:0", true));
        Assert.Throws<ArgumentException>(() => ProxyTargetRules.Parse("", true));
    }

    [Fact]
    public void OldProxyJsonHasNoImplicitDestinationLimit()
    {
        const string text = """{"id":"one","name":"Proxy","transport":"websocket","host":"relay.example.net","port":443,"path":"proxy","tls":{"serverName":"relay.example.net","pin":""},"auth":{"scheme":"bearer","credential":"test"},"updated":"2026-11-06T00:00:00Z"}""";
        var proxy = JsonSerializer.Deserialize<OfferedProxy>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(proxy);
        Assert.Null(proxy.Targets);
        Assert.True(ProxyTargetRules.Allows(proxy.Targets, "any.example.net", 51820));
    }
}
