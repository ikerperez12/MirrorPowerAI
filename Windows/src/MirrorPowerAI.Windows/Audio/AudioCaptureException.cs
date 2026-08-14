namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Identifies a recoverable loopback capture failure without exposing native error text to the UI.
/// </summary>
public enum AudioCaptureFailure
{
    /// <summary>No usable render endpoint is available.</summary>
    DeviceUnavailable,

    /// <summary>The selected endpoint was disconnected while capturing.</summary>
    DeviceDisconnected,

    /// <summary>The default endpoint changed while capturing.</summary>
    DefaultDeviceChanged,

    /// <summary>The bounded raw capture buffer was exhausted.</summary>
    BufferLimitReached,

    /// <summary>The underlying WASAPI session failed.</summary>
    BackendFailure,
}

/// <summary>
/// Represents an expected, recoverable Windows audio-capture error.
/// </summary>
public sealed class AudioCaptureException : Exception
{
    /// <summary>
    /// Initializes a capture exception.
    /// </summary>
    /// <param name="failure">Stable failure category.</param>
    /// <param name="message">Non-sensitive diagnostic message.</param>
    public AudioCaptureException(AudioCaptureFailure failure, string message)
        : base(message) => Failure = failure;

    /// <summary>
    /// Initializes a capture exception with its native cause.
    /// </summary>
    /// <param name="failure">Stable failure category.</param>
    /// <param name="message">Non-sensitive diagnostic message.</param>
    /// <param name="innerException">Native cause retained for local debugging.</param>
    public AudioCaptureException(
        AudioCaptureFailure failure,
        string message,
        Exception innerException)
        : base(message, innerException) => Failure = failure;

    /// <summary>Gets the stable capture failure category.</summary>
    public AudioCaptureFailure Failure { get; }
}
