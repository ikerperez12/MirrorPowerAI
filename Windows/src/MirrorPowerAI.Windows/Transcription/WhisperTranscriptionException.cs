namespace MirrorPowerAI.Windows.Transcription;

/// <summary>
/// Identifies a recoverable local transcription failure.
/// </summary>
public enum WhisperTranscriptionFailure
{
    /// <summary>The captured signal was silent.</summary>
    NoAudibleSignal,

    /// <summary>The audio payload was not the required normalized WAVE format.</summary>
    InvalidAudio,

    /// <summary>Whisper completed without producing text.</summary>
    EmptyTranscript,

    /// <summary>The local inference engine could not process the recording.</summary>
    InferenceFailed,
}

/// <summary>
/// Represents a safe, categorized local transcription error.
/// </summary>
public sealed class WhisperTranscriptionException : Exception
{
    /// <summary>
    /// Initializes a local transcription exception.
    /// </summary>
    /// <param name="failure">Stable failure category.</param>
    /// <param name="message">Non-sensitive user-facing message.</param>
    /// <param name="innerException">Underlying local error retained for diagnostics.</param>
    public WhisperTranscriptionException(
        WhisperTranscriptionFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    /// <summary>Gets the stable failure category.</summary>
    public WhisperTranscriptionFailure Failure { get; }
}
