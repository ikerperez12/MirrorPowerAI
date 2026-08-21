using MirrorPowerAI.Windows.Audio;

namespace MirrorPowerAI.Windows.Tests.Audio;

public sealed class LoopbackCaptureSessionOwnershipTests
{
    [Fact]
    public void TeardownPlan_NormalStop_StopsThenReleasesEveryResourceInOrder()
    {
        // Arrange
        var steps = new List<LoopbackCaptureTeardownStep>();

        // Act
        LoopbackCaptureTeardownPlan.Execute(steps.Add);

        // Assert
        Assert.Equal(
            [
                LoopbackCaptureTeardownStep.StopAudioClient,
                LoopbackCaptureTeardownStep.DisposeAudioCaptureClient,
                LoopbackCaptureTeardownStep.DisposeAudioClient,
                LoopbackCaptureTeardownStep.DisposeDevice,
                LoopbackCaptureTeardownStep.DisposeStopSignal,
            ],
            steps);
    }

    [Fact]
    public void RequestDispose_BeforeStart_ReservesCallerAndPreventsAnyLaterStart()
    {
        // Arrange
        var ownership = new LoopbackCaptureSessionOwnership();

        // Act
        var owner = ownership.RequestDispose(callingThreadId: 17);
        var repeatedOwner = ownership.RequestDispose(callingThreadId: 17);

        // Assert
        Assert.Equal(LoopbackCaptureTeardownOwner.CallerBeforeStart, owner);
        Assert.Equal(LoopbackCaptureTeardownOwner.None, repeatedOwner);
        Assert.Equal(17, ownership.TeardownThreadId);
        Assert.False(ownership.CanRequestStop());
        Assert.False(ownership.TryStart());

        ownership.CompleteTeardown(threadId: 17);

        Assert.True(ownership.IsReleased);
        Assert.False(ownership.CanRequestStop());
    }

    [Fact]
    public void CaptureLoop_NormalStop_OnlyItsOwnerMayReleaseStartedResources()
    {
        // Arrange
        var ownership = new LoopbackCaptureSessionOwnership();
        const int captureThreadId = 41;

        Assert.True(ownership.TryStart());
        Assert.True(ownership.TryClaimCaptureThread(captureThreadId));

        // Act
        var ownerBeganTeardown = ownership.TryBeginCaptureThreadTeardown(captureThreadId);

        // Assert
        Assert.True(ownerBeganTeardown);
        Assert.Equal(captureThreadId, ownership.TeardownThreadId);
        Assert.False(ownership.CanRequestStop());
        Assert.False(ownership.TryBeginCaptureThreadTeardown(threadId: 99));

        ownership.CompleteTeardown(captureThreadId);

        Assert.True(ownership.IsReleased);
    }

    [Fact]
    public void RequestDispose_FromForeignThread_OnlySignalsAndLeavesTeardownToCaptureThread()
    {
        // Arrange
        var ownership = new LoopbackCaptureSessionOwnership();
        const int captureThreadId = 41;
        const int disposingThreadId = 17;
        Assert.True(ownership.TryStart());
        Assert.True(ownership.TryClaimCaptureThread(captureThreadId));

        // Act
        var owner = ownership.RequestDispose(disposingThreadId);

        // Assert
        Assert.Equal(LoopbackCaptureTeardownOwner.CaptureThread, owner);
        Assert.Null(ownership.TeardownThreadId);
        Assert.True(ownership.CanRequestStop());
        Assert.False(ownership.TryBeginCaptureThreadTeardown(disposingThreadId));

        Assert.True(ownership.TryBeginCaptureThreadTeardown(captureThreadId));
        ownership.CompleteTeardown(captureThreadId);

        Assert.True(ownership.IsReleased);
    }

    [Fact]
    public void RecordJoinTimeout_CaptureThreadStillRetainsExclusiveTeardownOwnership()
    {
        // Arrange
        var ownership = new LoopbackCaptureSessionOwnership();
        const int captureThreadId = 41;
        const int disposingThreadId = 17;
        Assert.True(ownership.TryStart());
        Assert.True(ownership.TryClaimCaptureThread(captureThreadId));
        Assert.Equal(
            LoopbackCaptureTeardownOwner.CaptureThread,
            ownership.RequestDispose(disposingThreadId));

        // Act
        ownership.RecordJoinTimeout();

        // Assert
        Assert.True(ownership.JoinTimedOut);
        Assert.False(ownership.TryBeginCaptureThreadTeardown(disposingThreadId));
        Assert.True(ownership.TryBeginCaptureThreadTeardown(captureThreadId));

        ownership.CompleteTeardown(captureThreadId);

        Assert.True(ownership.IsReleased);
    }

    [Fact]
    public void TryBeginFailedStartTeardown_UsesCreatingThreadWhenWorkerNeverRan()
    {
        // Arrange
        var ownership = new LoopbackCaptureSessionOwnership();
        const int creatingThreadId = 17;
        Assert.True(ownership.TryStart());

        // Act
        var ownsTeardown = ownership.TryBeginFailedStartTeardown(creatingThreadId);

        // Assert
        Assert.True(ownsTeardown);
        Assert.Equal(creatingThreadId, ownership.TeardownThreadId);
        Assert.False(ownership.CanRequestStop());

        ownership.CompleteTeardown(creatingThreadId);

        Assert.True(ownership.IsReleased);
    }
}
