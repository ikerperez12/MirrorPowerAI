using System.Buffers.Binary;
using MirrorPowerAI.Windows.Audio;

namespace MirrorPowerAI.Windows.Tests.Audio;

public sealed class Pcm16WaveConverterTests
{
    [Fact]
    public void Convert_MonoPcm16_WritesRequiredWaveFormat()
    {
        // Arrange
        short[] sourceSamples = [-32_768, -16_384, 0, 16_384, 32_767];
        var source = Pcm16Bytes(sourceSamples);
        var converter = new Pcm16WaveConverter(0);

        // Act
        var result = converter.Convert(
            source,
            new AudioSampleFormat(16_000, 1, 16, AudioSampleEncoding.PcmInteger));

        // Assert
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(result.WavData, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(result.WavData, 8, 4));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(result.WavData.AsSpan(22, 2)));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(result.WavData.AsSpan(24, 4)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(result.WavData.AsSpan(34, 2)));
        Assert.Equal(sourceSamples.Length * sizeof(short), result.WavData.Length - 44);
        Assert.True(result.ContainsAudibleSignal);
    }

    [Fact]
    public void Convert_StereoPcm16_DownmixesChannelsToMono()
    {
        // Arrange: equal and opposite channels should cancel in the mono mix.
        short[] interleaved = [16_384, -16_384, 8_192, -8_192];
        var converter = new Pcm16WaveConverter(0.0001);

        // Act
        var result = converter.Convert(
            Pcm16Bytes(interleaved),
            new AudioSampleFormat(16_000, 2, 16, AudioSampleEncoding.PcmInteger));

        // Assert
        Assert.Equal(4, result.WavData.Length - 44);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(result.WavData.AsSpan(44, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(result.WavData.AsSpan(46, 2)));
        Assert.False(result.ContainsAudibleSignal);
    }

    [Fact]
    public void Convert_Float32_ClipsOutOfRangeAndReplacesNonFiniteValues()
    {
        // Arrange
        float[] samples = [2f, -2f, float.NaN, 0.5f];
        var source = new byte[samples.Length * sizeof(float)];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                source.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(samples[index]));
        }

        var converter = new Pcm16WaveConverter(0);

        // Act
        var result = converter.Convert(
            source,
            new AudioSampleFormat(16_000, 1, 32, AudioSampleEncoding.IeeeFloat));

        // Assert
        Assert.Equal(short.MaxValue, ReadOutputSample(result.WavData, 0));
        Assert.Equal(short.MinValue, ReadOutputSample(result.WavData, 1));
        Assert.Equal(0, ReadOutputSample(result.WavData, 2));
        Assert.Equal(16_384, ReadOutputSample(result.WavData, 3));
    }

    [Fact]
    public void Convert_PcmAt48Khz_ResamplesTo16KhzWithMatchingDuration()
    {
        // Arrange
        const int sourceSampleRate = 48_000;
        var samples = new short[sourceSampleRate];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (short)(Math.Sin(2 * Math.PI * 440 * index / sourceSampleRate) * 12_000);
        }

        var converter = new Pcm16WaveConverter();

        // Act
        var result = converter.Convert(
            Pcm16Bytes(samples),
            new AudioSampleFormat(sourceSampleRate, 1, 16, AudioSampleEncoding.PcmInteger));

        // Assert
        Assert.Equal(16_000 * sizeof(short), result.WavData.Length - 44);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Duration);
        Assert.True(result.ContainsAudibleSignal);
    }

    [Fact]
    public void Convert_UnalignedBuffer_ThrowsArgumentException()
    {
        // Arrange
        var converter = new Pcm16WaveConverter();
        var format = new AudioSampleFormat(48_000, 2, 16, AudioSampleEncoding.PcmInteger);

        // Act and assert
        _ = Assert.Throws<ArgumentException>(() => converter.Convert(new byte[3], format));
    }

    [Fact]
    public void ContainsAudibleSignal_SilentAndAudibleBuffers_UsesConfiguredThresholdWithoutAllocation()
    {
        // Arrange
        var converter = new Pcm16WaveConverter();
        var format = new AudioSampleFormat(16_000, 1, 16, AudioSampleEncoding.PcmInteger);

        // Act and assert
        Assert.False(converter.ContainsAudibleSignal(new byte[3_200], format));
        Assert.True(converter.ContainsAudibleSignal(Pcm16Bytes([8_000, -8_000]), format));
    }

    private static short ReadOutputSample(byte[] wave, int sampleIndex) =>
        BinaryPrimitives.ReadInt16LittleEndian(
            wave.AsSpan(44 + (sampleIndex * sizeof(short)), sizeof(short)));

    private static byte[] Pcm16Bytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(index * sizeof(short), sizeof(short)),
                samples[index]);
        }

        return bytes;
    }
}
