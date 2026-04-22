using CloudDrive.Core.SyncRoot;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncRoot;

public class MountStateMachineTests
{
    private readonly MountStateMachine _sut;

    public MountStateMachineTests()
    {
        var logger = new Mock<ILogger<MountStateMachine>>();
        _sut = new MountStateMachine(logger.Object);
    }

    [Fact]
    public void InitialPhase_ShouldBeIdle()
    {
        _sut.CurrentPhase.ShouldBe(MountPhase.Idle);
    }

    [Fact]
    public void ValidTransitions_FullHappyPath()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.CurrentPhase.ShouldBe(MountPhase.CheckingStale);

        _sut.TransitionTo(MountPhase.CleaningStale);
        _sut.CurrentPhase.ShouldBe(MountPhase.CleaningStale);

        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.CurrentPhase.ShouldBe(MountPhase.VerifyingConnection);

        _sut.TransitionTo(MountPhase.VerifyingListing);
        _sut.CurrentPhase.ShouldBe(MountPhase.VerifyingListing);

        _sut.TransitionTo(MountPhase.Registering);
        _sut.CurrentPhase.ShouldBe(MountPhase.Registering);

        _sut.TransitionTo(MountPhase.Ready);
        _sut.CurrentPhase.ShouldBe(MountPhase.Ready);
    }

    [Fact]
    public void ValidTransitions_NoStaleFound_SkipsCleaningStale()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.CurrentPhase.ShouldBe(MountPhase.VerifyingConnection);
    }

    [Fact]
    public void ValidTransitions_ConnectionFails_GoesToWaitingForServer()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.TransitionTo(MountPhase.WaitingForServer);
        _sut.CurrentPhase.ShouldBe(MountPhase.WaitingForServer);

        // Retry goes back to VerifyingConnection
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.CurrentPhase.ShouldBe(MountPhase.VerifyingConnection);
    }

    [Fact]
    public void ValidTransitions_ListingFails_GoesToWaitingForServer()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.TransitionTo(MountPhase.VerifyingListing);
        _sut.TransitionTo(MountPhase.WaitingForServer);
        _sut.CurrentPhase.ShouldBe(MountPhase.WaitingForServer);
    }

    [Fact]
    public void ValidTransitions_ConnectionLostAndRecovery()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.TransitionTo(MountPhase.VerifyingListing);
        _sut.TransitionTo(MountPhase.Registering);
        _sut.TransitionTo(MountPhase.Ready);
        _sut.TransitionTo(MountPhase.ConnectionLost);
        _sut.CurrentPhase.ShouldBe(MountPhase.ConnectionLost);

        // Reconnect
        _sut.TransitionTo(MountPhase.Ready);
        _sut.CurrentPhase.ShouldBe(MountPhase.Ready);
    }

    [Fact]
    public void InvalidTransition_IdleToReady_Throws()
    {
        Should.Throw<InvalidOperationException>(() =>
            _sut.TransitionTo(MountPhase.Ready));
    }

    [Fact]
    public void InvalidTransition_ReadyToCheckingStale_Throws()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.TransitionTo(MountPhase.VerifyingListing);
        _sut.TransitionTo(MountPhase.Registering);
        _sut.TransitionTo(MountPhase.Ready);

        Should.Throw<InvalidOperationException>(() =>
            _sut.TransitionTo(MountPhase.CheckingStale));
    }

    [Fact]
    public void ShuttingDown_ReachableFromAnyNonTerminalPhase()
    {
        // From Idle
        _sut.TransitionTo(MountPhase.ShuttingDown);
        _sut.CurrentPhase.ShouldBe(MountPhase.ShuttingDown);
    }

    [Fact]
    public void ShuttingDown_ReachableFromReady()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.TransitionTo(MountPhase.VerifyingListing);
        _sut.TransitionTo(MountPhase.Registering);
        _sut.TransitionTo(MountPhase.Ready);
        _sut.TransitionTo(MountPhase.ShuttingDown);
        _sut.CurrentPhase.ShouldBe(MountPhase.ShuttingDown);
    }

    [Fact]
    public void ShuttingDown_ReachableFromConnectionLost()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.TransitionTo(MountPhase.VerifyingListing);
        _sut.TransitionTo(MountPhase.Registering);
        _sut.TransitionTo(MountPhase.Ready);
        _sut.TransitionTo(MountPhase.ConnectionLost);
        _sut.TransitionTo(MountPhase.ShuttingDown);
        _sut.CurrentPhase.ShouldBe(MountPhase.ShuttingDown);
    }

    [Fact]
    public void ShuttingDown_ReachableFromWaitingForServer()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.TransitionTo(MountPhase.WaitingForServer);
        _sut.TransitionTo(MountPhase.ShuttingDown);
        _sut.CurrentPhase.ShouldBe(MountPhase.ShuttingDown);
    }

    [Fact]
    public void ShuttingDown_NotReachableFromError()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.CleaningStale);
        _sut.TransitionTo(MountPhase.StaleCleanupFailed);
        _sut.TransitionTo(MountPhase.Error);

        Should.Throw<InvalidOperationException>(() =>
            _sut.TransitionTo(MountPhase.ShuttingDown));
    }

    [Fact]
    public void ShuttingDown_NotReachableFromStopped()
    {
        _sut.TransitionTo(MountPhase.ShuttingDown);
        _sut.TransitionTo(MountPhase.Stopped);

        Should.Throw<InvalidOperationException>(() =>
            _sut.TransitionTo(MountPhase.ShuttingDown));
    }

    [Fact]
    public void TerminalPhases_Error_NoOutboundTransitions()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.CleaningStale);
        _sut.TransitionTo(MountPhase.StaleCleanupFailed);
        _sut.TransitionTo(MountPhase.Error);

        Should.Throw<InvalidOperationException>(() =>
            _sut.TransitionTo(MountPhase.Idle));
    }

    [Fact]
    public void TerminalPhases_Stopped_NoOutboundTransitions()
    {
        _sut.TransitionTo(MountPhase.ShuttingDown);
        _sut.TransitionTo(MountPhase.Stopped);

        Should.Throw<InvalidOperationException>(() =>
            _sut.TransitionTo(MountPhase.Idle));
    }

    [Fact]
    public void OnPhaseChanged_FiresOnValidTransition()
    {
        var firedPhases = new List<MountPhase>();
        _sut.OnPhaseChanged += phase => firedPhases.Add(phase);

        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);

        firedPhases.ShouldBe(new[] { MountPhase.CheckingStale, MountPhase.VerifyingConnection });
    }

    [Fact]
    public void OnPhaseChanged_DoesNotFireOnInvalidTransition()
    {
        var firedPhases = new List<MountPhase>();
        _sut.OnPhaseChanged += phase => firedPhases.Add(phase);

        Should.Throw<InvalidOperationException>(() =>
            _sut.TransitionTo(MountPhase.Ready));

        firedPhases.ShouldBeEmpty();
    }

    [Fact]
    public void StaleCleanupFailed_TransitionsToError()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.CleaningStale);
        _sut.TransitionTo(MountPhase.StaleCleanupFailed);
        _sut.TransitionTo(MountPhase.Error);
        _sut.CurrentPhase.ShouldBe(MountPhase.Error);
    }

    [Fact]
    public void Registering_CanTransitionToError()
    {
        _sut.TransitionTo(MountPhase.CheckingStale);
        _sut.TransitionTo(MountPhase.VerifyingConnection);
        _sut.TransitionTo(MountPhase.VerifyingListing);
        _sut.TransitionTo(MountPhase.Registering);
        _sut.TransitionTo(MountPhase.Error);
        _sut.CurrentPhase.ShouldBe(MountPhase.Error);
    }
}
