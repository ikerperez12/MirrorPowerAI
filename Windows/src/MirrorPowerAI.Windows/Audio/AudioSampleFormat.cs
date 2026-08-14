namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Identifies how individual samples are encoded in a raw interleaved audio buffer.
/// </summary>
public enum AudioSampleEncoding
{
    /// <summary>Signed little-endian PCM, except for unsigned 8-bit PCM.</summary>
    PcmInteger,

    /// <summary>IEEE 754 little-endian floating point.</summary>
    IeeeFloat,
}

/// <summary>
/// Describes an interleaved raw audio buffer without depending on NAudio types.
/// </summary>
public readonly record struct AudioSampleFormat
{
    /// <summary>
    /// Initializes a new audio format description.
    /// </summary>
    /// <param name="sampleRate">Samples per second for each channel.</param>
    /// <param name="channels">Number of interleaved channels.</param>
    /// <param name="bitsPerSample">Stored bits for each sample.</param>
    /// <param name="encoding">Sample encoding.</param>
    /// <exception cref="ArgumentOutOfRangeException">The format dimensions are invalid.</exception>
    /// <exception cref="ArgumentException">The bit depth is unsupported for the selected encoding.</exception>
    public AudioSampleFormat(
        int sampleRate,
        int channels,
        int bitsPerSample,
        AudioSampleEncoding encoding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        var supported = encoding switch
        {
            AudioSampleEncoding.PcmInteger => bitsPerSample is 8 or 16 or 24 or 32,
            AudioSampleEncoding.IeeeFloat => bitsPerSample is 32 or 64,
            _ => false,
        };

        if (!supported)
        {
            throw new ArgumentException(
                $"Unsupported {encoding} sample depth: {bitsPerSample} bits.",
                nameof(bitsPerSample));
        }

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        Encoding = encoding;
    }

    /// <summary>Gets the number of samples per second for each channel.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the number of interleaved channels.</summary>
    public int Channels { get; }

    /// <summary>Gets the stored number of bits per sample.</summary>
    public int BitsPerSample { get; }

    /// <summary>Gets the sample encoding.</summary>
    public AudioSampleEncoding Encoding { get; }

    /// <summary>Gets the number of bytes occupied by one sample.</summary>
    public int BytesPerSample => BitsPerSample / 8;

    /// <summary>Gets the byte alignment of one interleaved frame.</summary>
    public int BlockAlign => checked(BytesPerSample * Channels);

    /// <summary>Gets the number of bytes produced per second.</summary>
    public int AverageBytesPerSecond => checked(BlockAlign * SampleRate);
}
