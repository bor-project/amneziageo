using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The command line pointing one field of one rule: what it stores, what it refuses, and what the rule list
/// prints once a rule has somewhere else to ride.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class RoutingRuleCommandTests : IDisposable
{
    private const string ListName = "main";
    private const long ListId = 7;

    /// <inheritdoc />
    public void Dispose()
    {
        CliConsole.Restore();
    }

    [Fact]
    public async Task RuleTakesTheServerItIsPointedAt()
    {
        var agent = Agent(true, "proxy|geoip:ru");

        var (code, _) = await RunAsync(agent, "routing", "rule", "server", ListName, "geoip:ru", "de");

        Assert.Equal(Exit.Ok, code);
        Assert.Equal(["proxy|geoip:ru|server=de"], agent.Saved);
    }

    [Fact]
    public async Task FallbackIsStoredBesideTheServer()
    {
        var agent = Agent(true, "proxy|geoip:ru|server=de");

        var (code, _) = await RunAsync(agent, "routing", "rule", "fallback", ListName, "geoip:ru", "block");

        Assert.Equal(Exit.Ok, code);
        Assert.Equal(["proxy|geoip:ru|server=de|fallback=block"], agent.Saved);
    }

    [Fact]
    public async Task FieldPointedBackAtAuto_LeavesNothingBehindInTheRule()
    {
        var agent = Agent(true, "proxy|geoip:ru|server=de");

        var (code, _) = await RunAsync(agent, "routing", "rule", "server", ListName, "geoip:ru", "auto");

        Assert.Equal(Exit.Ok, code);
        Assert.Equal(["proxy|geoip:ru"], agent.Saved);
    }

    [Fact]
    public async Task ServerNameIsStoredTheWayTheLibrarySpellsIt()
    {
        var agent = Agent(true, "proxy|geoip:ru");

        await RunAsync(agent, "routing", "rule", "server", ListName, "geoip:ru", "DE");

        Assert.Equal(["proxy|geoip:ru|server=de"], agent.Saved);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("block")]
    public async Task ServerFieldTakesNoFallbackWord(string word)
    {
        var agent = Agent(true, "proxy|geoip:ru");

        var (code, _) = await RunAsync(agent, "routing", "rule", "server", ListName, "geoip:ru", word);

        Assert.Equal(Exit.Usage, code);
        Assert.Empty(agent.Saved);
    }

    [Fact]
    public async Task ServerTheLibraryDoesNotHold_IsRefused()
    {
        var agent = Agent(true, "proxy|geoip:ru");

        var (code, text) = await RunAsync(agent, "routing", "rule", "server", ListName, "geoip:ru", "se");

        Assert.Equal(Exit.Usage, code);
        Assert.Contains("is not a server", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOneServerAtATime_TheFieldIsNotOffered()
    {
        var agent = Agent(false, "proxy|geoip:ru");

        var (code, _) = await RunAsync(agent, "routing", "rule", "server", ListName, "geoip:ru", "de");

        Assert.Equal(Exit.Unsupported, code);
        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task RuleOutsideTheProxyBucket_RidesNoServer()
    {
        var agent = Agent(true, "direct|geoip:ru");

        var (code, text) = await RunAsync(agent, "routing", "rule", "server", ListName, "geoip:ru", "de");

        Assert.Equal(Exit.Usage, code);
        Assert.Contains("only a proxied rule rides a server", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleMatchedByName_TakesTheDefaultServer()
    {
        var agent = Agent(true, "proxy|geosite:openai");

        var (code, text) = await RunAsync(agent, "routing", "rule", "server", ListName, "geosite:openai", "de");

        Assert.Equal(Exit.Usage, code);
        Assert.Contains("matched by name", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleTheListDoesNotHold_IsRefused()
    {
        var agent = Agent(true, "proxy|geoip:ru");

        var (code, _) = await RunAsync(agent, "routing", "rule", "server", ListName, "geoip:cn", "de");

        Assert.Equal(Exit.Usage, code);
        Assert.Empty(agent.Saved);
    }

    [Fact]
    public async Task RuleListNamesTheFieldsOfEveryRule()
    {
        var agent = Agent(true, "proxy|geoip:ru|server=de|fallback=block");

        var (code, text) = await RunAsync(agent, "routing", "show", ListName);

        Assert.Equal(Exit.Ok, code);
        Assert.Contains("RULE", text, StringComparison.Ordinal);
        Assert.Contains("SERVER", text, StringComparison.Ordinal);
        Assert.Contains("FALLBACK", text, StringComparison.Ordinal);
        Assert.Contains("block", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOneServerAtATime_TheRuleListStaysAsItWas()
    {
        var agent = Agent(false, "proxy|geoip:ru");

        var (code, text) = await RunAsync(agent, "routing", "show", ListName);

        Assert.Equal(Exit.Ok, code);
        Assert.DoesNotContain("SERVER", text, StringComparison.Ordinal);
        Assert.Contains("proxy|geoip:ru", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FieldSetBeforeTheSwitchWentOff_StaysInSight()
    {
        var agent = Agent(false, "proxy|geoip:ru|server=de");

        var (_, text) = await RunAsync(agent, "routing", "show", ListName);

        Assert.Contains("SERVER", text, StringComparison.Ordinal);
        Assert.Contains("de", text, StringComparison.Ordinal);
    }

    private static Task<(int Code, string Text)> RunAsync(FakeCliAgent agent, params string[] args)
    {
        return CliConsole.RunAsync(agent, args);
    }

    // The agent as the console sees it: a library of two servers and one list holding the rules given here.
    private static FakeCliAgent Agent(bool multiServer, params string[] rules)
    {
        var snapshot = new StatusSnapshot(
            "1.0",
            null,
            [Config("fi"), Config("de")],
            [new RoutingListEntry(ListId, ListName, rules.Length, 0, 0)],
            MultiServer: multiServer);
        return new FakeCliAgent(snapshot, rules);
    }

    private static ConfigEntry Config(string name)
    {
        return new ConfigEntry(name, "vpn.example:51820", false, ConnectionStatus.Disconnected, []);
    }
}
