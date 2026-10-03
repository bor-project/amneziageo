using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A connect to another configuration under a connected tunnel asks the server of that configuration first, and
/// only a server that kept silent leaves the tunnel where it stands.
/// </summary>
public sealed class SwitchGuardTests
{
    [Fact]
    public void AConnectToAnotherConfigurationUnderAConnectedTunnel_NamesWhatStands()
    {
        Assert.Equal("home", SwitchGuard.Standing(true, ConnectionStatus.Connected, "home", "office"));
    }

    [Fact]
    public void AConnectWithNothingToKeep_AsksNoServer()
    {
        Assert.Null(SwitchGuard.Standing(true, ConnectionStatus.Connected, "home", "home"));
        Assert.Null(SwitchGuard.Standing(false, ConnectionStatus.Connected, "home", "office"));
        Assert.Null(SwitchGuard.Standing(true, ConnectionStatus.Connecting, "home", "office"));
        Assert.Null(SwitchGuard.Standing(true, ConnectionStatus.Connected, string.Empty, "office"));
        Assert.Null(SwitchGuard.Standing(true, ConnectionStatus.Connected, null, "office"));
    }

    [Fact]
    public void OnlyASilentServer_KeepsTheTunnel()
    {
        Assert.True(SwitchGuard.Keeps(SwitchVerdict.Silent));
        Assert.True(SwitchGuard.Keeps("silent\n"));
        Assert.False(SwitchGuard.Keeps(SwitchVerdict.Answered));
        Assert.False(SwitchGuard.Keeps(SwitchVerdict.Unknown));
        Assert.False(SwitchGuard.Keeps(string.Empty));
        Assert.False(SwitchGuard.Keeps(null));
    }

    [Fact]
    public void TheVerdict_NamesWhatTheEngineTold()
    {
        Assert.Equal(SwitchVerdict.Answered, SwitchVerdict.Of(true));
        Assert.Equal(SwitchVerdict.Silent, SwitchVerdict.Of(false));
        Assert.Equal(SwitchVerdict.Unknown, SwitchVerdict.Of(null));
    }

    [Fact]
    public async Task TheWordOfTheTunnel_IsWaitedFor()
    {
        var answers = new Queue<string>([string.Empty, string.Empty, "silent\n"]);
        var pauses = new List<int>();

        var said = await SwitchGuard.AwaitAsync(answers.Dequeue, 15_000, 250, pause =>
        {
            pauses.Add(pause);
            return Task.CompletedTask;
        });

        Assert.Equal(SwitchVerdict.Silent, said);
        Assert.Equal([250, 250, 250], pauses);
    }

    [Fact]
    public async Task ATunnelThatSaysNothing_LeavesTheVerdictUnknown()
    {
        var looks = 0;

        var said = await SwitchGuard.AwaitAsync(
            () =>
            {
                looks++;
                return string.Empty;
            },
            1_000,
            250,
            _ => Task.CompletedTask);

        Assert.Equal(SwitchVerdict.Unknown, said);
        Assert.Equal(4, looks);
    }
}
