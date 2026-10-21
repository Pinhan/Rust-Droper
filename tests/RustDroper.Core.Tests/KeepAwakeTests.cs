using RustDroper.Core.Orchestration;

namespace RustDroper.Core.Tests;

public class KeepAwakeTests
{
    private sealed class FakeAwake : IKeepAwake
    {
        public int Holds { get; private set; }

        public int Releases { get; private set; }

        public void Hold() => Holds++;

        public void Release() => Releases++;
    }

    private static DropperSnapshot Snap(EngineState state, bool running = true) =>
        new(
            AccountSnapshot.Initial,
            new EngineSnapshot(state, state.ToString(), null, null, null, 0, []),
            running,
            null,
            null,
            new HashSet<string>(),
            null);

    [Theory]
    [InlineData(EngineState.Starting, true)]
    [InlineData(EngineState.Discovering, true)]
    [InlineData(EngineState.Watching, true)]
    [InlineData(EngineState.ClaimPending, true)]
    [InlineData(EngineState.Degraded, true)]
    [InlineData(EngineState.Idle, false)]
    [InlineData(EngineState.AuthRequired, false)]
    [InlineData(EngineState.Stopped, false)]
    public void NeedsAwake_OnlyWhileTheEngineIsDoingWork(EngineState state, bool expected)
    {
        Assert.Equal(expected, KeepAwakeCoordinator.NeedsAwake(Snap(state)));
    }

    [Fact]
    public void NotRunning_NeverHolds_EvenIfStateLooksBusy()
    {
        Assert.False(KeepAwakeCoordinator.NeedsAwake(Snap(EngineState.Watching, running: false)));
    }

    [Fact]
    public void Holds_Once_ThenReleasesWhenTheEngineGoesIdle()
    {
        var awake = new FakeAwake();
        var coordinator = new KeepAwakeCoordinator(awake, () => true);

        coordinator.Evaluate(Snap(EngineState.Watching));
        coordinator.Evaluate(Snap(EngineState.Watching), Snap(EngineState.Idle));
        Assert.True(coordinator.IsHolding);
        Assert.Equal(1, awake.Holds);

        coordinator.Evaluate(Snap(EngineState.Idle), null);
        Assert.False(coordinator.IsHolding);
        Assert.Equal(1, awake.Releases);
    }

    [Fact]
    public void AnyOfTheServicesNeedingAwake_IsEnough()
    {
        var awake = new FakeAwake();
        var coordinator = new KeepAwakeCoordinator(awake, () => true);

        coordinator.Evaluate(Snap(EngineState.Idle), Snap(EngineState.Watching));

        Assert.Equal(1, awake.Holds);
    }

    [Fact]
    public void Setting_TurnedOff_ReleasesAndPreventsHolding()
    {
        var awake = new FakeAwake();
        var enabled = true;
        var coordinator = new KeepAwakeCoordinator(awake, () => enabled);
        coordinator.Evaluate(Snap(EngineState.Watching));

        enabled = false;
        coordinator.Evaluate(Snap(EngineState.Watching));
        coordinator.Evaluate(Snap(EngineState.Watching));

        Assert.False(coordinator.IsHolding);
        Assert.Equal(1, awake.Holds);
        Assert.Equal(1, awake.Releases);
    }

    [Fact]
    public void Shutdown_ReleasesAHeldRequest_AndIsIdempotent()
    {
        var awake = new FakeAwake();
        var coordinator = new KeepAwakeCoordinator(awake, () => true);
        coordinator.Evaluate(Snap(EngineState.Watching));

        coordinator.Shutdown();
        coordinator.Shutdown();

        Assert.Equal(1, awake.Releases);
        Assert.False(coordinator.IsHolding);
    }
}
