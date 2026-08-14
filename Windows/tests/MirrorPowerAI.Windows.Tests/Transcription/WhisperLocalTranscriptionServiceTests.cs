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
        var modelProvider = new FakeModelPathProvider();
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
    }

    [Fact]
    public async Task TranscribeAsync_SilentAudio_FailsBeforeModelAccess()
    {
        // Arrange
        var modelProvider = new FakeModelPathProvider();
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
        var modelProvider = new FakeModelPathProvider();
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
    public async Task TranscribeAsync_AutoLanguage_PassesExplicitAutoSelection()
    {
        // Arrange
        var engine = new FakeInferenceEngine("texto");
        var service = CreateService(new FakeModelPathProvider(), engine);

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
            new FakeModelPathProvider(),
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
            new FakeModelPathProvider(),
            new FakeInferenceEngine("unused"));

        // Act and assert
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TranscribeAsync(
                CreateAudio(containsAudibleSignal: true),
                "es",
                cancellation.Token));
    }

    private static WhisperLocalTranscriptionService CreateService(
        IWhisperModelPathProvider modelProvider,
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

    private sealed class FakeModelPathProvider : IWhisperModelPathProvider
    {
        public int CallCount { get; private set; }

        public Task<string> EnsureAvailableAsync(
            string modelDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(Path.Combine(modelDirectory, "verified-model.bin"));
        }
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
}
