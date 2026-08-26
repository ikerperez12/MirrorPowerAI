namespace MirrorPowerAI.Core.Audio;

/// <summary>
/// Identifies a recoverable audio-capture failure without exposing platform diagnostics to the UI.
/// </summary>
public enum AudioCaptureFailure
{
    /// <summary>No usable render source is available.</summary>
    SourceUnavailable,

    /// <summary>The selected output device or application ended while capturing.</summary>
    SourceDisconnected,

    /// <summary>The default output device changed while capturing.</summary>
    DefaultDeviceChanged,

    /// <summary>The bounded in-memory capture buffer reached its safety limit.</summary>
    BufferLimitReached,

    /// <summary>The platform audio backend failed.</summary>
    BackendFailure,
}

/// <summary>Represents an expected, recoverable capture error shared with session coordination.</summary>
public sealed class AudioCaptureException : Exception
{
    /// <summary>Initializes a capture exception.</summary>
    /// <param name="failure">Stable failure category.</param>
    /// <param name="message">Non-sensitive diagnostic message.</param>
    public AudioCaptureException(AudioCaptureFailure failure, string message)
        : base(message) => Failure = failure;

    /// <summary>Initializes a capture exception with its platform cause.</summary>
    /// <param name="failure">Stable failure category.</param>
    /// <param name="message">Non-sensitive diagnostic message.</param>
    /// <param name="innerException">Platform cause retained for local debugging.</param>
    public AudioCaptureException(
        AudioCaptureFailure failure,
        string message,
        Exception innerException)
        : base(message, innerException) => Failure = failure;

    /// <summary>Gets the stable capture failure category.</summary>
    public AudioCaptureFailure Failure { get; }
}
