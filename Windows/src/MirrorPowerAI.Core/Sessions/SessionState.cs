namespace MirrorPowerAI.Core.Sessions;

/// <summary>
/// Identifies the externally observable state of one MirrorPowerAI session.
/// </summary>
public enum SessionState
{
    /// <summary>No capture or processing is active.</summary>
    Idle,

    /// <summary>System output is being captured.</summary>
    Capturing,

    /// <summary>The captured audio is being transcribed.</summary>
    Transcribing,

    /// <summary>The transcript and context are being sent for an answer.</summary>
    RequestingAnswer,

    /// <summary>A completed answer is ready for display.</summary>
    ShowingResult,

    /// <summary>The last operation failed safely.</summary>
    Error,
}

/// <summary>
/// Describes one successful session without persisting its content.
/// </summary>
/// <param name="Transcript">The in-memory plain-text transcript.</param>
/// <param name="Answer">The in-memory plain-text answer.</param>
/// <param name="Provider">The explicitly selected transcription provider.</param>
/// <param name="CompletedAtUtc">The UTC completion time.</param>
public sealed record SessionResult(
    string Transcript,
    string Answer,
    Transcription.TranscriptionProvider Provider,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Identifies a safe session-level failure category.
/// </summary>
public enum SessionErrorKind
{
    /// <summary>Application configuration is invalid.</summary>
    InvalidConfiguration,

    /// <summary>The selected transcription provider is unavailable.</summary>
    ProviderUnavailable,

    /// <summary>No usable output audio was captured.</summary>
    EmptyAudio,

    /// <summary>The selected transcription provider produced no text.</summary>
    EmptyTranscript,

    /// <summary>The answer provider produced no text.</summary>
    EmptyAnswer,

    /// <summary>Explicit cloud audio consent is missing or outdated.</summary>
    ConsentRequired,

    /// <summary>A typed Gemini operation failed.</summary>
    Gemini,

    /// <summary>An unexpected adapter or platform failure occurred.</summary>
    Unexpected,
}

/// <summary>
/// Contains a safe failure message with no API key, context, transcript, or audio.
/// </summary>
/// <param name="Kind">The stable failure category.</param>
/// <param name="Message">A localized, non-sensitive explanation.</param>
public sealed record SessionFailure(SessionErrorKind Kind, string Message);

/// <summary>
/// Carries one session state transition.
/// </summary>
public sealed class SessionStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="previousState">The state before the transition.</param>
    /// <param name="currentState">The state after the transition.</param>
    public SessionStateChangedEventArgs(SessionState previousState, SessionState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    /// <summary>Gets the state before the transition.</summary>
    public SessionState PreviousState { get; }

    /// <summary>Gets the state after the transition.</summary>
    public SessionState CurrentState { get; }
}

/// <summary>
/// Indicates that another command is already changing or processing the session.
/// </summary>
public sealed class SessionBusyException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionBusyException"/> class.
    /// </summary>
    public SessionBusyException()
        : base("MirrorPowerAI ya está procesando otra operación de sesión.")
    {
    }
}
