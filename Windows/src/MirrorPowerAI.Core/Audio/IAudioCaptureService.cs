namespace MirrorPowerAI.Core.Audio;

/// <summary>
/// Captures the current system output and returns normalized in-memory audio.
/// </summary>
public interface IAudioCaptureService
{
    /// <summary>
    /// Gets a value indicating whether capture is active.
    /// </summary>
    bool IsCapturing { get; }

    /// <summary>
    /// Starts capturing system output.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel startup.</param>
    /// <returns>A task that completes when capture has started.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops capture and returns a mono, 16 kHz, 16-bit PCM WAV payload.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel finalization.</param>
    /// <returns>The normalized recording, held only in memory.</returns>
    Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default);
}
