namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Exposes only whether the active capture has observed usable audio, without exposing samples,
/// levels, device details, or any other sensitive capture data.
/// </summary>
public interface IAudioCaptureActivitySource
{
    /// <summary>
    /// Raised once per capture after a buffer first exceeds the same silence threshold used by
    /// final normalization.
    /// </summary>
    event EventHandler? AudibleSignalDetected;

    /// <summary>Gets whether usable audio has been observed in the active capture.</summary>
    bool HasDetectedAudibleSignal { get; }
}
