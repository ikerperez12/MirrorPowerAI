using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Transcription;
using MirrorPowerAI.Windows.Audio;
using MirrorPowerAI.Windows.Transcription;

namespace MirrorPowerAI.Windows.Tests.Transcription;

public sealed class WhisperLocalTranscriptionServiceTests
{
    [Fact]
    public async Task TranscribeAsync_ValidAudio_UsesVerifiedModelAndReturnsPlainText()
    {
        // Arrange
        var modelProvider = new FakeModelLeaseProvider();
        var engine = new FakeInferenceEngine("  transcripción local  ");
        var service = CreateService(modelProvider, engine);
        var audio = CreateAudio(containsAudibleSignal: true);

        // Act
        var transcript = await service.TranscribeAsync(audio, "ES");

        // Assert
        Assert.Equal(TranscriptionProvider.LocalWhisper, service.Provider);
        Assert.Equal("transcripción local", transcript);
        Assert.Equal(1, modelProvider.CallCount);
        Assert.Equal("es", engine.Language);
        Assert.Equal(4, engine.ThreadCount);
        Assert.True(modelProvider.Lease.IsDisposed);
    }

    [Fact]
    public async Task TranscribeAsync_SilentAudio_FailsBeforeModelAccess()
    {
        // Arrange
        var modelProvider = new FakeModelLeaseProvider();
        var service = CreateService(modelProvider, new FakeInferenceEngine("unused"));
        var audio = CreateAudio(containsAudibleSignal: false);

        // Act
        var exception = await Assert.ThrowsAsync<WhisperTranscriptionException>(
            () => service.TranscribeAsync(audio, "es"));

        // Assert
        Assert.Equal(WhisperTranscriptionFailure.NoAudibleSignal, exception.Failure);
        Assert.Equal(0, modelProvider.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_InvalidWave_FailsBeforeModelAccess()
    {
        // Arrange
        var modelProvider = new FakeModelLeaseProvider();
        var service = CreateService(modelProvider, new FakeInferenceEngine("unused"));
        var audio = new CapturedAudio(new byte[44], TimeSpan.Zero);

        // Act
        var exception = await Assert.ThrowsAsync<WhisperTranscriptionException>(
            () => service.TranscribeAsync(audio, "auto"));

        // Assert
        Assert.Equal(WhisperTranscriptionFailure.InvalidAudio, exception.Failure);
        Assert.Equal(0, modelProvider.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_DurationMetadataUnderstatesWave_FailsBeforeModelAccess()
    {
        var modelProvider = new FakeModelLeaseProvider();
        var service = CreateService(modelProvider, new FakeInferenceEngine("unused"));
        using var validAudio = CreateAudio(containsAudibleSignal: true);
        using var inconsistentAudio = new CapturedAudio(
            validAudio.WavData,
            TimeSpan.Zero,
            containsAudibleSignal: true);

        var exception = await Assert.ThrowsAsync<WhisperTranscriptionException>(
            () => service.TranscribeAsync(inconsistentAudio, "es"));

        Assert.Equal(WhisperTranscriptionFailure.InvalidAudio, exception.Failure);
        Assert.Equal(0, modelProvider.CallCount);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(28, 1)]
    [InlineData(32, 1)]
    public async Task TranscribeAsync_InconsistentWaveHeader_FailsBeforeModelAccess(
        int headerOffset,
        byte corruptValue)
    {
        var modelProvider = new FakeModelLeaseProvider();
        var service = CreateService(modelProvider, new FakeInferenceEngine("unused"));
        using var validAudio = CreateAudio(containsAudibleSignal: true);
        var corruptedWave = validAudio.WavData.ToArray();
        corruptedWave[headerOffset] = corruptValue;
        using var corruptedAudio = new CapturedAudio(
            corruptedWave,
            validAudio.Duration,
            containsAudibleSignal: true);

        var exception = await Assert.ThrowsAsync<WhisperTranscriptionException>(
            () => service.TranscribeAsync(corruptedAudio, "es"));

        Assert.Equal(WhisperTranscriptionFailure.InvalidAudio, exception.Failure);
        Assert.Equal(0, modelProvider.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyNormalizedWaveMarkedAudible_FailsBeforeModelAccess()
    {
        var modelProvider = new FakeModelLeaseProvider();
        var service = CreateService(modelProvider, new FakeInferenceEngine("unused"));
        var normalized = new Pcm16WaveConverter().Convert(
            ReadOnlySpan<byte>.Empty,
            new AudioSampleFormat(16_000, 1, 16, AudioSampleEncoding.PcmInteger));
        using var audio = new CapturedAudio(
            normalized.WavData,
            normalized.Duration,
            containsAudibleSignal: true);

        var exception = await Assert.ThrowsAsync<WhisperTranscriptionException>(
            () => service.TranscribeAsync(audio, "es"));

        Assert.Equal(WhisperTranscriptionFailure.InvalidAudio, exception.Failure);
        Assert.Equal(0, modelProvider.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_WaveLongerThanFiveMinutes_FailsBeforeModelAccess()
    {
        const int oversizedPcmLength = (16_000 * sizeof(short) * 300) + sizeof(short);
        var modelProvider = new FakeModelLeaseProvider();
        var service = CreateService(modelProvider, new FakeInferenceEngine("unused"));
        using var validAudio = CreateAudio(containsAudibleSignal: true);
        var oversizedWave = new byte[44 + oversizedPcmLength];
        validAudio.WavData.Span[..44].CopyTo(oversizedWave);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            oversizedWave.AsSpan(4, sizeof(uint)),
            36 + oversizedPcmLength);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            oversizedWave.AsSpan(40, sizeof(uint)),
            oversizedPcmLength);
        using var oversizedAudio = new CapturedAudio(
            oversizedWave,
            TimeSpan.FromSeconds((oversizedPcmLength / sizeof(short)) / 16_000d),
            containsAudibleSignal: true);

        var exception = await Assert.ThrowsAsync<WhisperTranscriptionException>(
            () => service.TranscribeAsync(oversizedAudio, "es"));

        Assert.Equal(WhisperTranscriptionFailure.InvalidAudio, exception.Failure);
        Assert.Equal(0, modelProvider.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_AutoLanguage_PassesExplicitAutoSelection()
    {
        // Arrange
        var engine = new FakeInferenceEngine("texto");
        var service = CreateService(new FakeModelLeaseProvider(), engine);

        // Act
        _ = await service.TranscribeAsync(CreateAudio(containsAudibleSignal: true), " AUTO ");

        // Assert
        Assert.Equal("auto", engine.Language);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyTranscript_ThrowsCategorizedFailure()
    {
        // Arrange
        var service = CreateService(
            new FakeModelLeaseProvider(),
            new FakeInferenceEngine("   "));

        // Act
        var exception = await Assert.ThrowsAsync<WhisperTranscriptionException>(
            () => service.TranscribeAsync(CreateAudio(containsAudibleSignal: true), "es"));

        // Assert
        Assert.Equal(WhisperTranscriptionFailure.EmptyTranscript, exception.Failure);
    }

    [Fact]
    public async Task TranscribeAsync_Cancelled_PropagatesCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService(
            new FakeModelLeaseProvider(),
            new FakeInferenceEngine("unused"));

        // Act and assert
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TranscribeAsync(
                CreateAudio(containsAudibleSignal: true),
                "es",
                cancellation.Token));
    }

    [Fact]
    public async Task TranscribeAsync_InferenceFailure_DisposesVerifiedModelLease()
    {
        // Arrange
        var modelProvider = new FakeModelLeaseProvider();
        var service = CreateService(modelProvider, new ThrowingInferenceEngine());

        // Act and assert
        _ = await Assert.ThrowsAsync<WhisperTranscriptionException>(() =>
            service.TranscribeAsync(CreateAudio(containsAudibleSignal: true), "es"));

        Assert.True(modelProvider.Lease.IsDisposed);
    }

    [Fact]
    public async Task TranscribeAsync_CancelledInference_RetainsLeaseUntilCancellationThenDisposesIt()
    {
        // Arrange
        var modelProvider = new FakeModelLeaseProvider();
        var engine = new BlockingInferenceEngine();
        var service = CreateService(modelProvider, engine);
        using var cancellation = new CancellationTokenSource();

        // Act
        var operation = service.TranscribeAsync(
            CreateAudio(containsAudibleSignal: true),
            "es",
            cancellation.Token);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.False(modelProvider.Lease.IsDisposed);
        cancellation.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(modelProvider.Lease.IsDisposed);
    }

    private static WhisperLocalTranscriptionService CreateService(
        IWhisperModelLeaseProvider modelProvider,
        IWhisperInferenceEngine engine) =>
        new(
            modelProvider,
            engine,
            Path.Combine(Path.GetTempPath(), "MirrorPowerAI.Tests", "Models"),
            threadCount: 4);

    private static CapturedAudio CreateAudio(bool containsAudibleSignal)
    {
        var source = new byte[3_200];
        if (containsAudibleSignal)
        {
            for (var index = 0; index < source.Length; index += sizeof(short))
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                    source.AsSpan(index, sizeof(short)),
                    index % 4 == 0 ? (short)8_000 : (short)-8_000);
            }
        }

        var normalized = new Pcm16WaveConverter().Convert(
            source,
            new AudioSampleFormat(16_000, 1, 16, AudioSampleEncoding.PcmInteger));
        return new CapturedAudio(
            normalized.WavData,
            normalized.Duration,
            containsAudibleSignal);
    }

    private sealed class FakeModelLeaseProvider : IWhisperModelLeaseProvider
    {
        public int CallCount { get; private set; }

        public FakeModelLease Lease { get; } = new();

        public Task<IWhisperModelLease> AcquireVerifiedLeaseAsync(
            string modelDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Lease.ModelPath = Path.Combine(modelDirectory, "verified-model.bin");
            return Task.FromResult<IWhisperModelLease>(Lease);
        }
    }

    private sealed class FakeModelLease : IWhisperModelLease
    {
        public string ModelPath { get; set; } = string.Empty;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeInferenceEngine(string transcript) : IWhisperInferenceEngine
    {
        public string? Language { get; private set; }

        public int ThreadCount { get; private set; }

        public Task<string> TranscribeAsync(
            string modelPath,
            ReadOnlyMemory<byte> wavData,
            string language,
            int threadCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Language = language;
            ThreadCount = threadCount;
            return Task.FromResult(transcript);
        }
    }

    private sealed class ThrowingInferenceEngine : IWhisperInferenceEngine
    {
        public Task<string> TranscribeAsync(
            string modelPath,
            ReadOnlyMemory<byte> wavData,
            string language,
            int threadCount,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("synthetic inference failure");
    }

    private sealed class BlockingInferenceEngine : IWhisperInferenceEngine
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> TranscribeAsync(
            string modelPath,
            ReadOnlyMemory<byte> wavData,
            string language,
            int threadCount,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "unreachable";
        }
    }
}
