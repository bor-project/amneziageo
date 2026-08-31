using System.Text;

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

    // Ключ у подписки свой на каждый узел; узлы с одним ключом - это один и тот же узел.
    private static string Key(string seed)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes((seed + " AmneziaGeo merge client").PadRight(32)[..32]));
    }

    private static VpnLinkCodec.Imported Node(
        string? name,
        string host = "example.net",
        int port = 51821,
        string? id = null,
        bool keyed = true)
    {
        var seed = id ?? name ?? host;
        var lines = new List<string> { "[Interface]" };
        if (keyed)
        {
            lines.Add($"PrivateKey = {Key(seed)}");
        }

        lines.AddRange(
        [
            "Address = 10.0.1.2/32",
            string.Empty,
            "[Peer]",
            $"PublicKey = {PeerKey}",
            "AllowedIPs = 0.0.0.0/0",
            $"Endpoint = {host}:{port}",
        ]);

        return new VpnLinkCodec.Imported(string.Join('\n', lines), name);
    }

    private static SubscriptionMember Member(SubscriptionChange change, string? configName = null)
    {
        return new SubscriptionMember("myvpn", change.Remark, configName ?? change.ConfigName);
    }

    private static Dictionary<string, string> Texts(params (string Name, VpnLinkCodec.Imported Node)[] items)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, node) in items)
        {
            texts[name] = node.ConfText;
        }

        return texts;
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
    public void UnnamedNode_TakesTheTagWithItsPort()
    {
        // Панель именует только первый узел, остальным достаётся общий хвост его имени.
        var plan = SubscriptionMerge.Plan(
            [Node("bor-work-pc-20-fi", port: 39847), Node("fi", port: 443)], [], []);

        Assert.Equal(["bor-work-pc-20-fi", "fi-443"], plan.Select(change => change.ConfigName));
    }

    [Fact]
    public void KnownNode_KeepsItsConfigAndIsRewritten()
    {
        var first = SubscriptionMerge.Plan([Node("phone")], [], []);
        var members = new[] { Member(first[0], "мой телефон") };

        var plan = SubscriptionMerge.Plan([Node("phone")], members, ["мой телефон"]);

        var change = Assert.Single(plan);
        Assert.Equal(SubscriptionChangeKind.Update, change.Kind);
        Assert.Equal("мой телефон", change.ConfigName);
    }

    [Fact]
    public void KnownNode_WhoseConfigWasDeleted_IsEnteredAgain()
    {
        var first = SubscriptionMerge.Plan([Node("phone")], [], []);
        var members = new[] { Member(first[0], "мой телефон") };

        var plan = SubscriptionMerge.Plan([Node("phone")], members, []);

        var change = Assert.Single(plan);
        Assert.Equal(SubscriptionChangeKind.Add, change.Kind);
        Assert.Equal("phone", change.ConfigName);
    }

    [Fact]
    public void NodeGoneFromTheFeed_IsMarked()
    {
        var first = SubscriptionMerge.Plan([Node("phone"), Node("laptop")], [], []);
        var members = first.Select(change => Member(change)).ToArray();

        var plan = SubscriptionMerge.Plan([Node("phone")], members, ["phone", "laptop"]);

        var gone = Assert.Single(plan, change => change.Kind == SubscriptionChangeKind.Gone);
        Assert.Equal("laptop", gone.ConfigName);
    }

    [Fact]
    public void NodeBackInTheFeed_IsRewrittenRatherThanDoubled()
    {
        var first = SubscriptionMerge.Plan([Node("phone")], [], []);
        var members = new[] { Member(first[0]) with { Present = false } };

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
    public void NodesSharingAName_AreBothEntered()
    {
        // Панель отдаёт нескольким узлам одно имя; узлы всё равно разные.
        var plan = SubscriptionMerge.Plan([Node("de", id: "one"), Node("de", id: "two")], [], []);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, change => Assert.Equal(SubscriptionChangeKind.Add, change.Kind));
        Assert.Equal(["de", "de-2"], plan.Select(change => change.ConfigName));
    }

    [Fact]
    public void RenamedNode_KeepsItsConfigAndIsRewritten()
    {
        var first = SubscriptionMerge.Plan([Node("phone")], [], []);
        var members = new[] { Member(first[0]) };
        var texts = Texts(("phone", Node("phone")));

        var plan = SubscriptionMerge.Plan([Node("phone-de", id: "phone", port: 51823)], members, ["phone"], texts);

        var change = Assert.Single(plan);
        Assert.Equal(SubscriptionChangeKind.Update, change.Kind);
        Assert.Equal("phone", change.ConfigName);
    }

    [Fact]
    public void RenamedNode_DoesNotTakeAnotherClientsConfig()
    {
        var first = SubscriptionMerge.Plan([Node("phone")], [], []);
        var members = new[] { Member(first[0]) };
        var texts = Texts(("phone", Node("phone")));

        var plan = SubscriptionMerge.Plan([Node("laptop-de", id: "laptop")], members, ["phone"], texts);

        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, change => change.Kind == SubscriptionChangeKind.Add);
        Assert.Contains(plan, change => change.Kind == SubscriptionChangeKind.Gone);
    }

    [Fact]
    public void MovedNode_IsKnownByItsClientKey()
    {
        var first = SubscriptionMerge.Plan([Node("phone")], [], []);
        var members = new[] { Member(first[0]) };

        var plan = SubscriptionMerge.Plan([Node("phone", "other.example.net")], members, ["phone"]);

        Assert.Equal(SubscriptionChangeKind.Update, Assert.Single(plan).Kind);
    }

    [Fact]
    public void RenamedNode_LeavesNothingBehindAsGone()
    {
        var first = SubscriptionMerge.Plan([Node("phone")], [], []);
        var members = new[] { Member(first[0]) };
        var texts = Texts(("phone", Node("phone")));

        var plan = SubscriptionMerge.Plan([Node("phone-de", id: "phone")], members, ["phone"], texts);

        Assert.DoesNotContain(plan, change => change.Kind == SubscriptionChangeKind.Gone);
    }

    [Fact]
    public void MemberKnownByItsOldName_MovesToTheKey()
    {
        // Запись прошлых версий: имя узла вместо ключа. Узнаётся по тексту заведённой конфигурации.
        var members = new[] { new SubscriptionMember("myvpn", "phone", "phone") };
        var texts = Texts(("phone", Node("phone")));

        var plan = SubscriptionMerge.Plan([Node("phone")], members, ["phone"], texts);

        var change = Assert.Single(plan);
        Assert.Equal(SubscriptionChangeKind.Update, change.Kind);
        Assert.Equal("phone", change.PreviousRemark);
        Assert.StartsWith("key:", change.Remark);
    }

    [Fact]
    public void NodeWithoutAKey_IsKnownByItsPeerAndHost()
    {
        var first = SubscriptionMerge.Plan([Node(null, keyed: false)], [], []);
        var members = new[] { Member(first[0]) };

        var second = SubscriptionMerge.Plan([Node(null, keyed: false)], members, [first[0].ConfigName]);

        Assert.Equal(SubscriptionChangeKind.Add, first[0].Kind);
        Assert.Equal("example.net", first[0].ConfigName);
        Assert.Equal(SubscriptionChangeKind.Update, Assert.Single(second).Kind);
    }

    [Fact]
    public void NodeWithoutAKeyThatMovedToAnotherHost_IsANewOne()
    {
        var first = SubscriptionMerge.Plan([Node(null, keyed: false)], [], []);
        var members = new[] { Member(first[0]) };

        var second = SubscriptionMerge.Plan([Node(null, "other.example.net", keyed: false)], members, [first[0].ConfigName]);

        Assert.Equal(2, second.Count);
        Assert.Contains(second, change => change.Kind == SubscriptionChangeKind.Add);
        Assert.Contains(second, change => change.Kind == SubscriptionChangeKind.Gone);
    }
}
