using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A connect stays dialled until the session stands: it waits for a network, repeats after a failure another
/// attempt gets past, and stops on one it does not or when it is taken back.
/// </summary>
public sealed class ConnectRetryTests
{
    [Theory]
    [InlineData(ConnectFailureReason.NoHandshake)]
    [InlineData(ConnectFailureReason.UnderlayUnreachable)]
    [InlineData(ConnectFailureReason.Timeout)]
    [InlineData(ConnectFailureReason.ServiceLaunchFailed)]
    [InlineData(ConnectFailureReason.Unknown)]
    public void ACauseOfTheNetworkOrTheServer_IsWorthAnotherAttempt(ConnectFailureReason reason)
    {
        Assert.True(ConnectRetry.IsTransient(reason));
    }

    [Theory]
    [InlineData(ConnectFailureReason.NoTargetSelected)]
    [InlineData(ConnectFailureReason.ConfigMissing)]
    [InlineData(ConnectFailureReason.ConfigInvalid)]
    [InlineData(ConnectFailureReason.ServiceStartFailed)]
    [InlineData(ConnectFailureReason.AdapterStartFailed)]
    [InlineData(ConnectFailureReason.TransportRejected)]
    [InlineData(ConnectFailureReason.PermissionDenied)]
    [InlineData(ConnectFailureReason.TooManyRoutes)]
    [InlineData(ConnectFailureReason.TunnelSetupFailed)]
    [InlineData(ConnectFailureReason.EngineStartFailed)]
    [InlineData(ConnectFailureReason.EngineUnavailable)]
    [InlineData(ConnectFailureReason.LoopbackBlocked)]
    public void ACauseOfThisDevice_IsNotRepeated(ConnectFailureReason reason)
    {
        Assert.False(ConnectRetry.IsTransient(reason));
    }

    [Fact]
    public void AnEngineThatStoppedByItself_IsDialledAgain()
    {
        Assert.True(ConnectRetry.IsTransient(ConnectFailureReason.EngineStopped));
    }

    [Fact]
    public async Task AServerThatAnswersOnTheSixthAttempt_IsDialledAfterEachPauseUntilItDoes()
    {
        var dial = new Dial { Outcomes = { Silent, Silent, Silent, Silent, Silent, DialOutcome.Raised } };

        var end = await ConnectRetry.RunAsync(dial.Steps(), CancellationToken.None);

        Assert.True(end?.Up);
        Assert.Equal(6, dial.Attempts);
        Assert.Equal(new[] { 0, 5, 10, 20, 60 }, dial.Pauses);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, dial.Retries);
    }

    [Fact]
    public async Task ANameThatDoesNotResolve_IsDialledAgain()
    {
        var dial = new Dial { Outcomes = { DialOutcome.Failed(ConnectFailureReason.Unknown, "the name did not resolve"), DialOutcome.Raised } };

        var end = await ConnectRetry.RunAsync(dial.Steps(), CancellationToken.None);

        Assert.True(end?.Up);
        Assert.Equal(2, dial.Attempts);
        Assert.Equal("the name did not resolve", dial.Causes.Single());
    }

    [Fact]
    public async Task AConnectStartedOnNoNetwork_WaitsForOneAndIsNotAttemptedBefore()
    {
        var dial = new Dial { Networks = { false, false, false, true }, Outcomes = { DialOutcome.Raised } };

        var end = await ConnectRetry.RunAsync(dial.Steps(), CancellationToken.None);

        Assert.True(end?.Up);
        Assert.Equal(1, dial.Attempts);
        Assert.Equal(new[] { "held", "pause 60", "pause 60", "pause 60", "released", "attempt" }, dial.Events);
    }

    [Fact]
    public async Task TheNetworkGoneAfterAFailure_HoldsTheNextAttemptUntilItIsBack()
    {
        var dial = new Dial { Networks = { true, false, true }, Outcomes = { Silent, DialOutcome.Raised } };

        var end = await ConnectRetry.RunAsync(dial.Steps(), CancellationToken.None);

        Assert.True(end?.Up);
        Assert.Equal(new[] { "attempt", "retry 1", "pause 0", "held", "pause 60", "released", "attempt" }, dial.Events);
    }

    [Fact]
    public async Task ACauseAnotherAttemptDoesNotGetPast_EndsTheDialWithIt()
    {
        var refused = DialOutcome.Failed(ConnectFailureReason.TooManyRoutes, "20000 of 13000");
        var dial = new Dial { Outcomes = { Silent, refused } };

        var end = await ConnectRetry.RunAsync(dial.Steps(), CancellationToken.None);

        Assert.Equal(refused, end);
        Assert.Equal(2, dial.Attempts);
        Assert.Equal(new[] { 1 }, dial.Retries);
    }

    [Fact]
    public async Task ADialTakenBackDuringAPause_EndsWithoutAFailureAndIsNotAttemptedAgain()
    {
        using var taken = new CancellationTokenSource();
        var dial = new Dial { Outcomes = { Silent, Silent, Silent }, OnPause = pauses => { if (pauses == 2) { taken.Cancel(); } } };

        var end = await ConnectRetry.RunAsync(dial.Steps(), taken.Token);

        Assert.Null(end);
        Assert.Equal(2, dial.Attempts);
    }

    [Fact]
    public async Task ADialTakenBackDuringAnAttempt_ReportsNeitherAFailureNorARetry()
    {
        using var taken = new CancellationTokenSource();
        var dial = new Dial { Outcomes = { Silent }, OnAttempt = taken.Cancel };

        var end = await ConnectRetry.RunAsync(dial.Steps(), taken.Token);

        Assert.Null(end);
        Assert.Empty(dial.Retries);
    }

    [Fact]
    public async Task ADialTakenBackWhileItWaitsForANetwork_IsNeverAttempted()
    {
        using var taken = new CancellationTokenSource();
        var dial = new Dial { Networks = { false }, OnPause = _ => taken.Cancel() };

        var end = await ConnectRetry.RunAsync(dial.Steps(), taken.Token);

        Assert.Null(end);
        Assert.Equal(0, dial.Attempts);
    }

    private static DialOutcome Silent => DialOutcome.Failed(ConnectFailureReason.NoHandshake, "no handshake");

    // A platform under the dial: what each look at the network and each attempt gives, and what was asked of it.
    private sealed class Dial
    {
        public List<bool> Networks { get; } = [];

        public List<DialOutcome> Outcomes { get; } = [];

        public List<string> Events { get; } = [];

        public List<int> Pauses { get; } = [];

        public List<int> Retries { get; } = [];

        public List<string> Causes { get; } = [];

        public int Attempts { get; private set; }

        public Action? OnAttempt { get; init; }

        public Action<int>? OnPause { get; init; }

        private int _looks;

        public DialSteps Steps()
        {
            return new DialSteps(Look, Attempt, Pause, () => Events.Add("held"), () => Events.Add("released"), Retry);
        }

        // The network as the list gives it look by look; the last entry stands for every look past it.
        private bool Look()
        {
            if (Networks.Count == 0)
            {
                return true;
            }

            var seen = Networks[Math.Min(_looks, Networks.Count - 1)];
            _looks++;
            return seen;
        }

        private Task<DialOutcome> Attempt(CancellationToken ct)
        {
            Events.Add("attempt");
            var outcome = Outcomes[Math.Min(Attempts, Outcomes.Count - 1)];
            Attempts++;
            OnAttempt?.Invoke();
            return Task.FromResult(outcome);
        }

        private Task Pause(TimeSpan delay, CancellationToken ct)
        {
            Pauses.Add((int)delay.TotalSeconds);
            Events.Add($"pause {(int)delay.TotalSeconds}");
            OnPause?.Invoke(Pauses.Count);
            return Task.CompletedTask;
        }

        private void Retry(int failures, TimeSpan delay, DialOutcome outcome)
        {
            Retries.Add(failures);
            Causes.Add(outcome.Detail);
            Events.Add($"retry {failures}");
        }
    }
}
