namespace MirrorPowerAI.Windows.Shell;

/// <summary>
/// Narrow adapter between the platform shell and the Core session controller.
/// </summary>
public interface ISessionCommands
{
    /// <summary>Raised whenever user-visible state or the latest protected result changes.</summary>
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>Gets the latest shell-safe snapshot.</summary>
    SessionSnapshot Snapshot { get; }

    /// <summary>Starts capture from idle or stops the active capture session.</summary>
    /// <param name="cancellationToken">Cancels the requested transition.</param>
    /// <returns>A task that completes after the Core transition.</returns>
    Task ToggleAsync(CancellationToken cancellationToken);

    /// <summary>Cancels capture or processing without starting another session.</summary>
    /// <param name="cancellationToken">Cancels the wait to issue cancellation.</param>
    /// <returns>A task that completes after active work stops.</returns>
    Task CancelAsync(CancellationToken cancellationToken);

    /// <summary>Cancels active work, disposes the controller that captured the current privacy settings, and clears its result.</summary>
    /// <param name="cancellationToken">Cancels the wait to invalidate the controller.</param>
    /// <returns>A task that completes after no session can continue with stale settings.</returns>
    Task ResetAsync(CancellationToken cancellationToken);
}

/// <summary>Coarse activity values understood by the Windows shell.</summary>
public enum ShellActivityState
{
    /// <summary>No session is running.</summary>
    Idle,

    /// <summary>Output audio is being captured.</summary>
    Capturing,

    /// <summary>Transcription or answer generation is in progress.</summary>
    Processing,

    /// <summary>The last transition failed and requires user attention.</summary>
    Error,
}

/// <summary>
/// Contains the minimum state the shell needs without depending on implementation details in Core.
/// </summary>
/// <param name="Activity">Current activity.</param>
/// <param name="Question">Latest transcription; only rendered in a protected overlay.</param>
/// <param name="Answer">Latest answer; only rendered in a protected overlay.</param>
/// <param name="UserMessage">Resource key for a sanitized status or error message.</param>
public sealed record SessionSnapshot(
    ShellActivityState Activity,
    string? Question = null,
    string? Answer = null,
    string? UserMessage = null)
{
    /// <summary>Gets whether a complete result is available for protected display.</summary>
    public bool HasResult => !string.IsNullOrWhiteSpace(Answer);
}

/// <summary>Provides a new immutable session snapshot to the WPF shell.</summary>
public sealed class SessionStateChangedEventArgs : EventArgs
{
    /// <summary>Initializes state-change event data.</summary>
    /// <param name="snapshot">The new immutable snapshot.</param>
    public SessionStateChangedEventArgs(SessionSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <summary>Gets the new session snapshot.</summary>
    public SessionSnapshot Snapshot { get; }
}

/// <summary>
/// Safe placeholder used until composition connects the Core <c>SessionController</c> adapter.
/// </summary>
public sealed class UnavailableSessionCommands : ISessionCommands
{
    /// <inheritdoc />
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public SessionSnapshot Snapshot { get; private set; } = new(ShellActivityState.Idle);

    /// <inheritdoc />
    public Task ToggleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Snapshot = new SessionSnapshot(ShellActivityState.Error, UserMessage: "SessionUnavailable");
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(Snapshot));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Snapshot = new SessionSnapshot(ShellActivityState.Idle);
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(Snapshot));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResetAsync(CancellationToken cancellationToken) => CancelAsync(cancellationToken);
}
