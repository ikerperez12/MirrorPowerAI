namespace MirrorPowerAI.Core.Audio;

/// <summary>
/// Exposes short, normalized speech segments while a capture session remains active.
/// </summary>
/// <remarks>
/// The event is intentionally optional on <see cref="IAudioCaptureService"/> so existing
/// adapters can still provide one-shot capture. Subscribers own each <see cref="CapturedAudio"/>
/// delivered through the event and must dispose it after processing.
/// </remarks>
public interface IAudioSegmentSource
{
    /// <summary>
    /// Occurs when a short in-memory segment is ready for transcription.  A segment marked with
    /// <see cref="AudioSegmentAvailableEventArgs.ForcedBoundary"/> was cut by the safety cap and
    /// should not be treated as a complete conversational turn by default.
    /// </summary>
    event EventHandler<AudioSegmentAvailableEventArgs>? SegmentAvailable;
}

/// <summary>Carries one short normalized audio segment from an active capture.</summary>
public sealed class AudioSegmentAvailableEventArgs : EventArgs
{
    /// <summary>Initializes event data.</summary>
    /// <param name="audio">The normalized audio segment; the subscriber owns its lifetime.</param>
    /// <param name="forcedBoundary">
    /// Indicates that the segment was cut by the bounded maximum rather than by a conversational
    /// pause.  Consumers can use this to join a short trailing fragment to the next segment.
    /// </param>
    public AudioSegmentAvailableEventArgs(CapturedAudio audio, bool forcedBoundary = false)
    {
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        ForcedBoundary = forcedBoundary;
    }

    /// <summary>Gets the in-memory segment owned by the subscriber.</summary>
    public CapturedAudio Audio { get; }

    /// <summary>
    /// Gets a value indicating whether the segment ended because its bounded maximum was reached.
    /// </summary>
    public bool ForcedBoundary { get; }
}
