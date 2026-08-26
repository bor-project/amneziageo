using System.Text.Json;
using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The command line reading the layout the distributor came to: what it tables, and what it says where nothing is
/// split at all.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class RoutingPlanCommandTests : IDisposable
{
    /// <inheritdoc />
    public void Dispose()
    {
        CliConsole.Restore();
    }

    [Fact]
    public async Task LayoutNamesWhereEveryRuleGoesAndWhy()
    {
        var agent = Agent(Split());

        var (code, text) = await CliConsole.RunAsync(agent, "routing", "plan");

        Assert.Equal(Exit.Ok, code);
        Assert.Contains("RULE", text, StringComparison.Ordinal);
        Assert.Contains("GOES", text, StringComparison.Ordinal);
        Assert.Contains("WHY", text, StringComparison.Ordinal);
        Assert.Contains("proxy|geoip:ru|server=de", text, StringComparison.Ordinal);
        Assert.Contains("Named", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryServerIsTabledByWhatItCarries()
    {
        var agent = Agent(Split());

        var (_, text) = await CliConsole.RunAsync(agent, "routing", "plan");

        Assert.Contains("SERVER", text, StringComparison.Ordinal);
        Assert.Contains("LIST", text, StringComparison.Ordinal);
        Assert.Contains("everything besides", text, StringComparison.Ordinal);
        Assert.Contains("list 'main'", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesGoingPastTheTunnel_AreCountedUnderTheTable()
    {
        var agent = Agent(Split() with { Direct = 2, Blocked = 1 });

        var (_, text) = await CliConsole.RunAsync(agent, "routing", "plan");

        Assert.Contains("2 rule(s) go past the tunnel, 1 dropped", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithSeveralServersOff_TheLayoutStopsAtTheTunnels()
    {
        var agent = Agent(new RoutingLayout(false, string.Empty, [new ServerLayout("fi", "main", 3, true)], [], 0, 0));

        var (code, text) = await CliConsole.RunAsync(agent, "routing", "plan");

        Assert.Equal(Exit.Ok, code);
        Assert.Contains("each tunnel carries the whole list it is bound to", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WHY", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LayoutAsJson_CarriesTheWholeAnswer()
    {
        var agent = Agent(Split());

        var (code, text) = await CliConsole.RunAsync(agent, "--json", "routing", "plan");

        Assert.Equal(Exit.Ok, code);
        Assert.Contains("\"multiServer\"", text, StringComparison.Ordinal);
        Assert.Contains("\"reason\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentThatAnswersWithNothingReadable_IsReported()
    {
        var agent = Agent(Split());
        agent.Answers(IpcContract.OpGetRoutingLayout, "not a layout");

        var (code, _) = await CliConsole.RunAsync(agent, "routing", "plan");

        Assert.Equal(Exit.Failed, code);
    }

    // Two servers up, one rule apiece: the one that carries everything and the one a rule names.
    private static RoutingLayout Split()
    {
        return new RoutingLayout(
            true,
            "main",
            [new ServerLayout("fi", "main", 1, true), new ServerLayout("de", "main", 1, false)],
            [
                new RuleLayout("proxy|geoip:cn", "auto", "fi", "Auto"),
                new RuleLayout("proxy|geoip:ru|server=de", "server", "de", "Named"),
            ],
            0,
            0);
    }

    private static FakeCliAgent Agent(RoutingLayout layout)
    {
        var agent = new FakeCliAgent(new StatusSnapshot("1.0", null, []), []);
        agent.Answers(IpcContract.OpGetRoutingLayout, JsonSerializer.Serialize(layout, IpcJson.Options));
        return agent;
    }
}
