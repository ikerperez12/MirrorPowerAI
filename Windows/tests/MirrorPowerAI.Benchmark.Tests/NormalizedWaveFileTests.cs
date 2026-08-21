using System.Buffers.Binary;
using MirrorPowerAI.Benchmark;

namespace MirrorPowerAI.Benchmark.Tests;

public sealed class NormalizedWaveFileTests
{
    [Fact]
    public void Open_CanonicalNormalizedWave_ReturnsExactDurationAndRewindsStream()
    {
        using var file = new TemporaryFile(CreateWave(sampleRate: 16_000, dataByteCount: 320));

        using var wave = NormalizedWaveFile.Open(file.Path);

        Assert.Equal(TimeSpan.FromMilliseconds(10), wave.Duration);
        Assert.Equal(0, wave.Stream.Position);
    }

    [Fact]
    public void Open_WrongSampleRate_RejectsInputBeforeInference()
    {
        using var file = new TemporaryFile(CreateWave(sampleRate: 44_100, dataByteCount: 882));

        var exception = Assert.Throws<InvalidDataException>(() => NormalizedWaveFile.Open(file.Path));

        Assert.Contains("16 kHz", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_TruncatedDataChunk_RejectsInputBeforeInference()
    {
        var waveBytes = CreateWave(sampleRate: 16_000, dataByteCount: 320);
        Array.Resize(ref waveBytes, waveBytes.Length - 2);
        using var file = new TemporaryFile(waveBytes);

        var exception = Assert.Throws<InvalidDataException>(() => NormalizedWaveFile.Open(file.Path));

        Assert.Contains("RIFF declarado", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_DurationBeyondFiveMinutes_RejectsInputBeforeInference()
    {
        using var file = new TemporaryFile(CreateWave(sampleRate: 16_000, dataByteCount: 9_600_002));

        var exception = Assert.Throws<InvalidDataException>(() => NormalizedWaveFile.Open(file.Path));

        Assert.Contains("cinco minutos", exception.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateWave(int sampleRate, int dataByteCount)
    {
        const int headerLength = 44;
        const ushort channelCount = 1;
        const ushort bitsPerSample = 16;
        const ushort blockAlignment = channelCount * (bitsPerSample / 8);
        var wave = new byte[headerLength + dataByteCount];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4, 4), checked((uint)(wave.Length - 8)));
        "WAVE"u8.CopyTo(wave.AsSpan(8));
        "fmt "u8.CopyTo(wave.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), channelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24, 4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(
            wave.AsSpan(28, 4),
            checked((uint)(sampleRate * blockAlignment)));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), blockAlignment);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), bitsPerSample);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40, 4), checked((uint)dataByteCount));
        return wave;
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(byte[] contents)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"MirrorPowerAI-Benchmark-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(Path, contents);
        }

        public string Path { get; }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
