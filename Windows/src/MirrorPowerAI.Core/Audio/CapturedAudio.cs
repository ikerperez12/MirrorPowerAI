namespace MirrorPowerAI.Core.Audio;

/// <summary>
/// Represents an in-memory WAV recording normalized for speech recognition.
/// </summary>
public sealed class CapturedAudio : IDisposable
{
    private readonly byte[] _wavData;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapturedAudio"/> class.
    /// </summary>
    /// <param name="wavData">The complete WAV payload.</param>
    /// <param name="duration">The duration represented by the payload.</param>
    /// <param name="containsAudibleSignal">
    /// A value indicating whether the capture implementation detected a non-silent signal.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="duration"/> is negative.
    /// </exception>
    public CapturedAudio(
        ReadOnlyMemory<byte> wavData,
        TimeSpan duration,
        bool containsAudibleSignal = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        _wavData = wavData.ToArray();
        Duration = duration;
        ContainsAudibleSignal = containsAudibleSignal;
    }

    /// <summary>
    /// Gets the required sample rate in hertz.
    /// </summary>
    public const int SampleRate = 16_000;

    /// <summary>
    /// Gets the required channel count.
    /// </summary>
    public const int Channels = 1;

    /// <summary>
    /// Gets the required PCM sample size in bits.
    /// </summary>
    public const int BitsPerSample = 16;

    /// <summary>
    /// Gets the complete normalized WAV payload.
    /// </summary>
    public ReadOnlyMemory<byte> WavData => _wavData;

    /// <summary>
    /// Gets the recording duration.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// Gets a value indicating whether the capture contains a non-silent signal.
    /// </summary>
    public bool ContainsAudibleSignal { get; }

    /// <summary>
    /// Clears the in-memory WAV buffer so audio is not retained longer than the active operation.
    /// </summary>
    public void Dispose()
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_wavData);
        GC.SuppressFinalize(this);
    }
}
