using System.Buffers.Binary;

namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Converts raw PCM or IEEE-float audio to an in-memory mono, 16 kHz, 16-bit PCM WAVE file.
/// </summary>
public sealed class Pcm16WaveConverter
{
    /// <summary>The output sample rate required by the transcription engines.</summary>
    public const int OutputSampleRate = 16_000;

    /// <summary>The output channel count.</summary>
    public const int OutputChannels = 1;

    /// <summary>The output PCM bit depth.</summary>
    public const int OutputBitsPerSample = 16;

    private const int WaveHeaderLength = 44;
    private readonly double _silenceRootMeanSquareThreshold;

    /// <summary>
    /// Initializes a converter.
    /// </summary>
    /// <param name="silenceRootMeanSquareThreshold">
    /// Linear full-scale RMS below which audio is classified as silence.
    /// </param>
    public Pcm16WaveConverter(double silenceRootMeanSquareThreshold = 0.001)
    {
        if (!double.IsFinite(silenceRootMeanSquareThreshold)
            || silenceRootMeanSquareThreshold < 0
            || silenceRootMeanSquareThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceRootMeanSquareThreshold));
        }

        _silenceRootMeanSquareThreshold = silenceRootMeanSquareThreshold;
    }

    /// <summary>
    /// Converts a complete raw interleaved buffer to the normalized WAVE representation.
    /// </summary>
    /// <param name="rawAudio">Raw source bytes.</param>
    /// <param name="sourceFormat">Description of the raw source bytes.</param>
    /// <returns>The normalized WAVE payload and its signal metadata.</returns>
    /// <exception cref="ArgumentException">The buffer is not frame aligned.</exception>
    /// <exception cref="InvalidOperationException">The converted payload would exceed the in-memory WAVE limit.</exception>
    public NormalizedWaveAudio Convert(ReadOnlySpan<byte> rawAudio, AudioSampleFormat sourceFormat)
    {
        if (rawAudio.Length % sourceFormat.BlockAlign != 0)
        {
            throw new ArgumentException("The raw audio buffer is not aligned to complete frames.", nameof(rawAudio));
        }

        var sourceFrameCount = rawAudio.Length / sourceFormat.BlockAlign;
        var outputFrameCountLong = sourceFrameCount == 0
            ? 0
            : Math.Max(1L, (long)Math.Round(
                sourceFrameCount * (double)OutputSampleRate / sourceFormat.SampleRate,
                MidpointRounding.AwayFromZero));

        if (outputFrameCountLong > (int.MaxValue - WaveHeaderLength) / sizeof(short))
        {
            throw new InvalidOperationException("The converted audio is too large for an in-memory WAVE payload.");
        }

        var outputFrameCount = (int)outputFrameCountLong;
        var pcmByteCount = checked(outputFrameCount * sizeof(short));
        var wave = new byte[checked(WaveHeaderLength + pcmByteCount)];
        WriteWaveHeader(wave, pcmByteCount);

        double squareSum = 0;
        for (var outputIndex = 0; outputIndex < outputFrameCount; outputIndex++)
        {
            var sample = ResampleMono(rawAudio, sourceFormat, sourceFrameCount, outputIndex);
            var pcmSample = ToPcm16(sample);
            BinaryPrimitives.WriteInt16LittleEndian(
                wave.AsSpan(WaveHeaderLength + (outputIndex * sizeof(short)), sizeof(short)),
                pcmSample);

            var normalized = pcmSample < 0 ? pcmSample / 32768d : pcmSample / 32767d;
            squareSum += normalized * normalized;
        }

        var rootMeanSquare = outputFrameCount == 0
            ? 0
            : Math.Sqrt(squareSum / outputFrameCount);
        var duration = TimeSpan.FromSeconds(outputFrameCount / (double)OutputSampleRate);

        return new NormalizedWaveAudio(
            wave,
            duration,
            rootMeanSquare >= _silenceRootMeanSquareThreshold);
    }

    private static double ResampleMono(
        ReadOnlySpan<byte> rawAudio,
        AudioSampleFormat format,
        int sourceFrameCount,
        int outputIndex)
    {
        if (sourceFrameCount == 0)
        {
            return 0;
        }

        if (format.SampleRate <= OutputSampleRate)
        {
            var sourcePosition = outputIndex * (double)format.SampleRate / OutputSampleRate;
            var firstFrame = Math.Min((int)sourcePosition, sourceFrameCount - 1);
            var secondFrame = Math.Min(firstFrame + 1, sourceFrameCount - 1);
            var fraction = sourcePosition - firstFrame;
            var first = ReadMonoFrame(rawAudio, format, firstFrame);
            var second = ReadMonoFrame(rawAudio, format, secondFrame);
            return first + ((second - first) * fraction);
        }

        // A weighted box filter prevents the worst aliasing when reducing the sample rate.
        var intervalStart = outputIndex * (double)format.SampleRate / OutputSampleRate;
        var intervalEnd = Math.Min(
            (outputIndex + 1d) * format.SampleRate / OutputSampleRate,
            sourceFrameCount);
        var firstSourceFrame = (int)Math.Floor(intervalStart);
        var lastSourceFrame = Math.Min((int)Math.Ceiling(intervalEnd) - 1, sourceFrameCount - 1);

        double weightedSum = 0;
        double totalWeight = 0;
        for (var sourceFrame = firstSourceFrame; sourceFrame <= lastSourceFrame; sourceFrame++)
        {
            var overlapStart = Math.Max(intervalStart, sourceFrame);
            var overlapEnd = Math.Min(intervalEnd, sourceFrame + 1d);
            var weight = Math.Max(0, overlapEnd - overlapStart);
            weightedSum += ReadMonoFrame(rawAudio, format, sourceFrame) * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : 0;
    }

    private static double ReadMonoFrame(
        ReadOnlySpan<byte> rawAudio,
        AudioSampleFormat format,
        int frameIndex)
    {
        double sum = 0;
        var frameOffset = checked(frameIndex * format.BlockAlign);
        for (var channel = 0; channel < format.Channels; channel++)
        {
            var sampleOffset = frameOffset + (channel * format.BytesPerSample);
            var sampleBytes = rawAudio.Slice(sampleOffset, format.BytesPerSample);
            sum += ReadSample(sampleBytes, format);
        }

        return ClampFinite(sum / format.Channels);
    }

    private static double ReadSample(ReadOnlySpan<byte> sample, AudioSampleFormat format)
    {
        if (format.Encoding == AudioSampleEncoding.IeeeFloat)
        {
            var value = format.BitsPerSample == 32
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample))
                : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(sample));
            return ClampFinite(value);
        }

        return format.BitsPerSample switch
        {
            8 => (sample[0] - 128) / 128d,
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
            24 => ReadPcm24(sample) / 8_388_608d,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2_147_483_648d,
            _ => throw new InvalidOperationException("The validated source bit depth became unsupported."),
        };
    }

    private static int ReadPcm24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xFF00_0000);
    }

    private static double ClampFinite(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        return Math.Clamp(value, -1d, 1d);
    }

    private static short ToPcm16(double sample)
    {
        var clamped = ClampFinite(sample);
        return clamped < 0
            ? (short)Math.Round(clamped * 32768d, MidpointRounding.AwayFromZero)
            : (short)Math.Round(clamped * 32767d, MidpointRounding.AwayFromZero);
    }

    private static void WriteWaveHeader(Span<byte> wave, int pcmByteCount)
    {
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave[4..8], checked((uint)(36 + pcmByteCount)));
        "WAVE"u8.CopyTo(wave[8..]);
        "fmt "u8.CopyTo(wave[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(wave[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave[20..22], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave[22..24], OutputChannels);
        BinaryPrimitives.WriteUInt32LittleEndian(wave[24..28], OutputSampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            wave[28..32],
            OutputSampleRate * OutputChannels * (OutputBitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(
            wave[32..34],
            OutputChannels * (OutputBitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(wave[34..36], OutputBitsPerSample);
        "data"u8.CopyTo(wave[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(wave[40..44], checked((uint)pcmByteCount));
    }
}
