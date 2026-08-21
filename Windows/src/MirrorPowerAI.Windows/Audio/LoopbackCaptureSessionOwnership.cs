namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Identifies the only thread that may release a loopback session's native resources.
/// </summary>
internal enum LoopbackCaptureTeardownOwner
{
    /// <summary>No teardown has been reserved.</summary>
    None,

    /// <summary>The creating thread releases a session that was never started.</summary>
    CallerBeforeStart,

    /// <summary>The capture thread releases a session that has started.</summary>
    CaptureThread,
}

/// <summary>
/// Enumerates the required native teardown order after a WASAPI loopback session stops.
/// </summary>
internal enum LoopbackCaptureTeardownStep
{
    /// <summary>Stops the audio client before any COM wrapper is released.</summary>
    StopAudioClient,

    /// <summary>Releases the capture service queried from the audio client.</summary>
    DisposeAudioCaptureClient,

    /// <summary>Releases the WASAPI audio client.</summary>
    DisposeAudioClient,

    /// <summary>Releases the endpoint device.</summary>
    DisposeDevice,

    /// <summary>Releases the managed stop signal after native teardown.</summary>
    DisposeStopSignal,
}

/// <summary>
/// Holds the thread-agnostic state machine that assigns ownership of loopback teardown.
/// </summary>
/// <remarks>
/// This type deliberately has no NAudio dependency so ownership and timeout decisions remain
/// deterministic under unit test. It does not execute cleanup itself; the owner selected here
/// is responsible for executing <see cref="LoopbackCaptureTeardownPlan"/> exactly once.
/// </remarks>
internal sealed class LoopbackCaptureSessionOwnership
{
    private readonly object _sync = new();
    private LifecycleState _state = LifecycleState.Ready;
    private LoopbackCaptureTeardownOwner _reservedTeardownOwner;
    private int _captureThreadId;
    private int _teardownThreadId;
    private bool _disposeRequested;
    private bool _joinTimedOut;

    /// <summary>
    /// Attempts to reserve this session for a single capture-thread start.
    /// </summary>
    /// <returns><see langword="true"/> when the caller may start the capture thread.</returns>
    public bool TryStart()
    {
        lock (_sync)
        {
            if (_state != LifecycleState.Ready || _disposeRequested)
            {
                return false;
            }

            _state = LifecycleState.Started;
            return true;
        }
    }

    /// <summary>
    /// Claims the capture-thread-only teardown responsibility after the thread begins.
    /// </summary>
    /// <param name="threadId">Managed identifier of the running capture thread.</param>
    /// <returns><see langword="true"/> when the capture thread owns this started session.</returns>
    public bool TryClaimCaptureThread(int threadId)
    {
        ValidateThreadId(threadId);
        lock (_sync)
        {
            if (_state != LifecycleState.Started)
            {
                return false;
            }

            _captureThreadId = threadId;
            _state = LifecycleState.Capturing;
            return true;
        }
    }

    /// <summary>
    /// Determines whether a caller may set the stop signal without racing signal disposal.
    /// </summary>
    /// <returns><see langword="true"/> before teardown begins; otherwise <see langword="false"/>.</returns>
    public bool CanRequestStop()
    {
        lock (_sync)
        {
            return _state is LifecycleState.Ready or LifecycleState.Started or LifecycleState.Capturing;
        }
    }

    /// <summary>
    /// Requests disposal and selects the thread that is permitted to release native resources.
    /// </summary>
    /// <param name="callingThreadId">Managed identifier of the disposing caller.</param>
    /// <returns>The owner that must complete teardown, or <see cref="LoopbackCaptureTeardownOwner.None"/>.</returns>
    public LoopbackCaptureTeardownOwner RequestDispose(int callingThreadId)
    {
        ValidateThreadId(callingThreadId);
        lock (_sync)
        {
            if (_state == LifecycleState.Released)
            {
                return LoopbackCaptureTeardownOwner.None;
            }

            _disposeRequested = true;
            if (_state == LifecycleState.Ready)
            {
                ReserveTeardown(LoopbackCaptureTeardownOwner.CallerBeforeStart, callingThreadId);
                return LoopbackCaptureTeardownOwner.CallerBeforeStart;
            }

            if (_state == LifecycleState.Releasing)
            {
                // A capture thread may already be releasing while a foreign caller waits for it.
                // A caller-owned pre-start cleanup, by contrast, must never be executed twice.
                return _reservedTeardownOwner == LoopbackCaptureTeardownOwner.CaptureThread
                    ? LoopbackCaptureTeardownOwner.CaptureThread
                    : LoopbackCaptureTeardownOwner.None;
            }

            return LoopbackCaptureTeardownOwner.CaptureThread;
        }
    }

    /// <summary>
    /// Reserves teardown for the capture thread after an orderly or failed capture loop exits.
    /// </summary>
    /// <param name="threadId">Managed identifier of the running capture thread.</param>
    /// <returns><see langword="true"/> only for the thread that claimed capture ownership.</returns>
    public bool TryBeginCaptureThreadTeardown(int threadId)
    {
        ValidateThreadId(threadId);
        lock (_sync)
        {
            if (_state != LifecycleState.Capturing || _captureThreadId != threadId)
            {
                return false;
            }

            ReserveTeardown(LoopbackCaptureTeardownOwner.CaptureThread, threadId);
            return true;
        }
    }

    /// <summary>
    /// Reserves caller teardown when starting the capture thread itself failed before it ran.
    /// </summary>
    /// <param name="threadId">Managed identifier of the caller that attempted to start the thread.</param>
    /// <returns><see langword="true"/> when the caller must release the pre-capture resources.</returns>
    public bool TryBeginFailedStartTeardown(int threadId)
    {
        ValidateThreadId(threadId);
        lock (_sync)
        {
            if (_state != LifecycleState.Started || _captureThreadId != 0)
            {
                return false;
            }

            ReserveTeardown(LoopbackCaptureTeardownOwner.CallerBeforeStart, threadId);
            return true;
        }
    }

    /// <summary>
    /// Marks the selected owner's teardown as complete.
    /// </summary>
    /// <param name="threadId">Managed identifier of the thread that performed teardown.</param>
    public void CompleteTeardown(int threadId)
    {
        ValidateThreadId(threadId);
        lock (_sync)
        {
            if (_state != LifecycleState.Releasing || _teardownThreadId != threadId)
            {
                throw new InvalidOperationException("Only the reserved teardown owner can complete loopback cleanup.");
            }

            _state = LifecycleState.Released;
        }
    }

    /// <summary>
    /// Records that a foreign disposer stopped waiting, while preserving capture-thread ownership.
    /// </summary>
    public void RecordJoinTimeout()
    {
        lock (_sync)
        {
            if (_state != LifecycleState.Released)
            {
                _joinTimedOut = true;
            }
        }
    }

    /// <summary>Gets whether a foreign caller exhausted its bounded join.</summary>
    public bool JoinTimedOut
    {
        get
        {
            lock (_sync)
            {
                return _joinTimedOut;
            }
        }
    }

    /// <summary>Gets whether native teardown has completed.</summary>
    public bool IsReleased
    {
        get
        {
            lock (_sync)
            {
                return _state == LifecycleState.Released;
            }
        }
    }

    /// <summary>Gets the thread currently reserved to execute teardown, when any.</summary>
    public int? TeardownThreadId
    {
        get
        {
            lock (_sync)
            {
                return _state == LifecycleState.Releasing ? _teardownThreadId : null;
            }
        }
    }

    private void ReserveTeardown(LoopbackCaptureTeardownOwner owner, int threadId)
    {
        _reservedTeardownOwner = owner;
        _teardownThreadId = threadId;
        _state = LifecycleState.Releasing;
    }

    private static void ValidateThreadId(int threadId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadId, 1);
    }

    private enum LifecycleState
    {
        Ready,
        Started,
        Capturing,
        Releasing,
        Released,
    }
}

/// <summary>
/// Executes the only permitted release ordering for a loopback session.
/// </summary>
internal static class LoopbackCaptureTeardownPlan
{
    /// <summary>
    /// Invokes <paramref name="executeStep"/> in the required native-resource teardown order.
    /// </summary>
    /// <param name="executeStep">Action that performs one resource-specific cleanup step.</param>
    public static void Execute(Action<LoopbackCaptureTeardownStep> executeStep)
    {
        ArgumentNullException.ThrowIfNull(executeStep);

        executeStep(LoopbackCaptureTeardownStep.StopAudioClient);
        executeStep(LoopbackCaptureTeardownStep.DisposeAudioCaptureClient);
        executeStep(LoopbackCaptureTeardownStep.DisposeAudioClient);
        executeStep(LoopbackCaptureTeardownStep.DisposeDevice);
        executeStep(LoopbackCaptureTeardownStep.DisposeStopSignal);
    }
}
