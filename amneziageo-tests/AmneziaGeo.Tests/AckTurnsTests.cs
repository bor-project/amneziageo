using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The agent answers the commands of a connection in the order they came, with no id to tell the answers apart: a
/// waiter that gave up must not leave its answer to the command sent after it.
/// </summary>
public sealed class AckTurnsTests
{
    [Fact]
    public async Task TheAckOfACommandItsWaiterGaveUpOn_DoesNotAnswerTheNextOne()
    {
        var turns = new AckTurns();
        var download = turns.Expect();
        Assert.True(turns.Abandon(download));

        var show = turns.Expect();
        turns.Deliver(new IpcAck(true, "3 source(s) updated"));
        Assert.False(show.Task.IsCompleted);

        turns.Deliver(new IpcAck(true, "[Interface]"));
        Assert.Equal("[Interface]", (await show.Task).Message);
    }

    [Fact]
    public async Task AnAckThatBeatTheTimeout_IsTheCommandsOwn_AndNothingIsOwed()
    {
        var turns = new AckTurns();
        var first = turns.Expect();
        turns.Deliver(new IpcAck(true, "first"));

        Assert.False(turns.Abandon(first));
        Assert.Equal("first", (await first.Task).Message);

        var next = turns.Expect();
        turns.Deliver(new IpcAck(true, "next"));
        Assert.Equal("next", (await next.Task).Message);
    }

    [Fact]
    public async Task ADroppedConnection_OwesNothingToTheNextOne()
    {
        var turns = new AckTurns();
        var lost = turns.Expect();
        turns.Abandon(lost);
        turns.Fail(new IpcAck(false, "disconnected"));

        var fresh = turns.Expect();
        turns.Deliver(new IpcAck(true, "fresh"));
        Assert.Equal("fresh", (await fresh.Task).Message);
    }

    [Fact]
    public async Task ACommandThatNeverLeft_OwesNoAck()
    {
        var turns = new AckTurns();
        turns.Withdraw(turns.Expect());

        var next = turns.Expect();
        turns.Deliver(new IpcAck(true, "next"));
        Assert.Equal("next", (await next.Task).Message);
    }

    [Fact]
    public async Task TheWaitingCommand_LearnsTheConnectionDropped()
    {
        var turns = new AckTurns();
        var waiting = turns.Expect();

        turns.Fail(new IpcAck(false, "disconnected"));

        Assert.Equal("disconnected", (await waiting.Task).Message);
    }
}
