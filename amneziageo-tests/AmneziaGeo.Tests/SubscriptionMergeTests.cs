using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Сведение прочитанной подписки с библиотекой: что завести, что переписать, что пометить пропавшим.
/// </summary>
public sealed class SubscriptionMergeTests
{
    private const string PeerKey = "QW1uZXppYUdlbyBtZXJnZSBzZXJ2ZXIga2V5IDAwMSE=";

    private static VpnLinkCodec.Imported Node(string? name, string host = "example.net")
    {
        var text = string.Join(
            '\n',
            [
                "[Interface]",
                "PrivateKey = QW1uZXppYUdlbyBtZXJnZSBjbGllbnQga2V5IDAwMSE=",
                "Address = 10.0.1.2/32",
                string.Empty,
                "[Peer]",
                $"PublicKey = {PeerKey}",
                "AllowedIPs = 0.0.0.0/0",
                $"Endpoint = {host}:51821",
            ]);

        return new VpnLinkCodec.Imported(text, name);
    }

    [Fact]
    public void FirstRead_EntersEveryNode()
    {
        var plan = SubscriptionMerge.Plan([Node("phone"), Node("laptop")], [], []);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, change => Assert.Equal(SubscriptionChangeKind.Add, change.Kind));
        Assert.Equal(["phone", "laptop"], plan.Select(change => change.ConfigName));
    }

    [Fact]
    public void NameAlreadyTaken_GetsADashedSuffix()
    {
        // Скобочный суффикс дал бы имя, которое не принимает адаптер туннеля.
        var plan = SubscriptionMerge.Plan([Node("phone")], [], ["phone"]);

        var change = Assert.Single(plan);
        Assert.Equal("phone-2", change.ConfigName);
    }

    [Fact]
    public void KnownNode_KeepsItsConfigAndIsRewritten()
    {
        var members = new[] { new SubscriptionMember("myvpn", "phone", "мой телефон") };

        var plan = SubscriptionMerge.Plan([Node("phone")], members, ["мой телефон"]);

        var change = Assert.Single(plan);
        Assert.Equal(SubscriptionChangeKind.Update, change.Kind);
        Assert.Equal("мой телефон", change.ConfigName);
    }

    [Fact]
    public void KnownNode_WhoseConfigWasDeleted_IsEnteredAgain()
    {
        var members = new[] { new SubscriptionMember("myvpn", "phone", "мой телефон") };

        var plan = SubscriptionMerge.Plan([Node("phone")], members, []);

        var change = Assert.Single(plan);
        Assert.Equal(SubscriptionChangeKind.Add, change.Kind);
        Assert.Equal("phone", change.ConfigName);
    }

    [Fact]
    public void NodeGoneFromTheFeed_IsMarked()
    {
        var members = new[]
        {
            new SubscriptionMember("myvpn", "phone", "phone"),
            new SubscriptionMember("myvpn", "laptop", "laptop"),
        };

        var plan = SubscriptionMerge.Plan([Node("phone")], members, ["phone", "laptop"]);

        var gone = Assert.Single(plan, change => change.Kind == SubscriptionChangeKind.Gone);
        Assert.Equal("laptop", gone.ConfigName);
    }

    [Fact]
    public void NodeBackInTheFeed_IsRewrittenRatherThanDoubled()
    {
        var members = new[] { new SubscriptionMember("myvpn", "phone", "phone", Present: false) };

        var plan = SubscriptionMerge.Plan([Node("phone")], members, ["phone"]);

        var change = Assert.Single(plan);
        Assert.Equal(SubscriptionChangeKind.Update, change.Kind);
    }

    [Fact]
    public void SameNodeTwiceInTheFeed_IsEnteredOnce()
    {
        var plan = SubscriptionMerge.Plan([Node("phone"), Node("phone")], [], []);

        Assert.Single(plan);
    }

    [Fact]
    public void NodeWithoutARemark_IsKnownByItsPeerAndHost()
    {
        var first = SubscriptionMerge.Plan([Node(null)], [], []);
        var member = new SubscriptionMember("myvpn", first[0].Remark, first[0].ConfigName);

        var second = SubscriptionMerge.Plan([Node(null)], [member], [first[0].ConfigName]);

        Assert.Equal(SubscriptionChangeKind.Add, first[0].Kind);
        Assert.Equal("example.net", first[0].ConfigName);
        Assert.Equal(SubscriptionChangeKind.Update, Assert.Single(second).Kind);
    }

    [Fact]
    public void NodeThatMovedToAnotherHost_IsANewOne()
    {
        var first = SubscriptionMerge.Plan([Node(null)], [], []);
        var member = new SubscriptionMember("myvpn", first[0].Remark, first[0].ConfigName);

        var second = SubscriptionMerge.Plan([Node(null, "other.example.net")], [member], [first[0].ConfigName]);

        Assert.Equal(2, second.Count);
        Assert.Contains(second, change => change.Kind == SubscriptionChangeKind.Add);
        Assert.Contains(second, change => change.Kind == SubscriptionChangeKind.Gone);
    }
}
