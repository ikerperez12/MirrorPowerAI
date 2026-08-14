namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Contains normalized 16 kHz, mono, 16-bit PCM wave audio.
/// </summary>
/// <param name="WavData">Complete in-memory RIFF/WAVE payload.</param>
/// <param name="Duration">Duration represented by the PCM data.</param>
/// <param name="ContainsAudibleSignal">Whether the signal exceeds the configured silence threshold.</param>
public sealed record NormalizedWaveAudio(
    byte[] WavData,
    TimeSpan Duration,
    bool ContainsAudibleSignal);
