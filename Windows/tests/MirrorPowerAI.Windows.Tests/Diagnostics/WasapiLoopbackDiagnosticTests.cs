using System.Buffers.Binary;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Windows.Diagnostics;

namespace MirrorPowerAI.Windows.Tests.Diagnostics;

public sealed class WasapiLoopbackDiagnosticTests
{
    [Fact]
    public async Task VerifyAsync_SilentCaptureWithSamples_AllowsNormalModeAndClearsCapturedAudio()
    {
        // Arrange
        var audio = CreateNormalizedAudio(containsAudibleSignal: false);
        var captureService = new FakeAudioCaptureService(audio);
        TimeSpan? delayedFor = null;
        var diagnostic = new WasapiLoopbackDiagnostic((duration, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            delayedFor = duration;
            return Task.CompletedTask;
        });

        // Act
        var result = await diagnostic.VerifyAsync(
            captureService,
            TimeSpan.FromMilliseconds(100),
            requireAudibleSignal: false);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.False(result.ContainsAudibleSignal);
        Assert.Equal(TimeSpan.FromMilliseconds(100), delayedFor);
        Assert.Equal(1, captureService.StartCount);
        Assert.Equal(1, captureService.StopCount);
        Assert.Equal(1, captureService.AsyncDisposeCount);
        Assert.False(captureService.IsCapturing);
        AssertAudioWasCleared(audio);
    }

    [Fact]
    public async Task VerifyAsync_AudibleCaptureWithStrictRequirement_Succeeds()
    {
        // Arrange
        var audio = CreateNormalizedAudio(containsAudibleSignal: true);
        var captureService = new FakeAudioCaptureService(audio);
        var diagnostic = new WasapiLoopbackDiagnostic(CompleteDelayAsync);

        // Act
        var result = await diagnostic.VerifyAsync(
            captureService,
            TimeSpan.FromMilliseconds(100),
            requireAudibleSignal: true);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.True(result.ContainsAudibleSignal);
        AssertAudioWasCleared(audio);
    }

    [Fact]
    public async Task VerifyAsync_CaptureDurationIsTooShort_ThrowsAndClearsCapturedAudio()
    {
        // Arrange
        var audio = CreateNormalizedAudio(
            containsAudibleSignal: true,
            duration: TimeSpan.FromMilliseconds(99));
        var captureService = new FakeAudioCaptureService(audio);
        var diagnostic = new WasapiLoopbackDiagnostic(CompleteDelayAsync);

        // Act and assert
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostic.VerifyAsync(
            captureService,
            TimeSpan.FromMilliseconds(100),
            requireAudibleSignal: false));

        Assert.Equal(1, captureService.StopCount);
        Assert.Equal(1, captureService.AsyncDisposeCount);
        AssertAudioWasCleared(audio);
    }

    [Fact]
    public async Task VerifyAsync_CaptureHasInsufficientWaveData_ThrowsAndClearsCapturedAudio()
    {
        // Arrange
        var audio = new CapturedAudio(new byte[44], TimeSpan.FromMilliseconds(100));
        var captureService = new FakeAudioCaptureService(audio);
        var diagnostic = new WasapiLoopbackDiagnostic(CompleteDelayAsync);

        // Act and assert
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostic.VerifyAsync(
            captureService,
            TimeSpan.FromMilliseconds(100),
            requireAudibleSignal: false));

        Assert.Equal(1, captureService.StopCount);
        Assert.Equal(1, captureService.AsyncDisposeCount);
        AssertAudioWasCleared(audio);
    }

    [Fact]
    public async Task VerifyAsync_CapturePayloadShorterThanMinimum_ThrowsAndClearsCapturedAudio()
    {
        // Arrange
        var audio = CreateNormalizedAudio(
            containsAudibleSignal: true,
            sampleByteCount: 2);
        var captureService = new FakeAudioCaptureService(audio);
        var diagnostic = new WasapiLoopbackDiagnostic(CompleteDelayAsync);

        // Act and assert
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostic.VerifyAsync(
            captureService,
            TimeSpan.FromMilliseconds(100),
            requireAudibleSignal: false));

        Assert.Equal(1, captureService.StopCount);
        Assert.Equal(1, captureService.AsyncDisposeCount);
        AssertAudioWasCleared(audio);
    }

    [Fact]
    public async Task VerifyAsync_SilentCaptureWithStrictRequirement_ReturnsUnsuccessfulResult()
    {
        // Arrange
        var audio = CreateNormalizedAudio(containsAudibleSignal: false);
        var captureService = new FakeAudioCaptureService(audio);
        var diagnostic = new WasapiLoopbackDiagnostic(CompleteDelayAsync);

        // Act
        var result = await diagnostic.VerifyAsync(
            captureService,
            TimeSpan.FromMilliseconds(100),
            requireAudibleSignal: true);

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.False(result.ContainsAudibleSignal);
        Assert.Equal(1, captureService.StopCount);
        AssertAudioWasCleared(audio);
    }

    [Fact]
    public async Task VerifyAsync_StartFailsAfterActivatingCapture_PropagatesFailureAndCleansUp()
    {
        // Arrange
        var audio = CreateNormalizedAudio(containsAudibleSignal: true);
        var startError = new InvalidOperationException("simulated start failure");
        var captureService = new FakeAudioCaptureService(
            audio,
            startError: startError,
            keepCapturingWhenStartFails: true);
        var diagnostic = new WasapiLoopbackDiagnostic(CompleteDelayAsync);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostic.VerifyAsync(
            captureService,
            TimeSpan.FromMilliseconds(100),
            requireAudibleSignal: false));

        // Assert
        Assert.Same(startError, exception);
        Assert.Equal(1, captureService.StartCount);
        Assert.Equal(1, captureService.StopCount);
        Assert.Equal(1, captureService.AsyncDisposeCount);
        Assert.False(captureService.IsCapturing);
        AssertAudioWasCleared(audio);
    }

    [Fact]
    public async Task VerifyAsync_StopFails_PropagatesFailureAndRetriesCleanup()
    {
        // Arrange
        var audio = CreateNormalizedAudio(containsAudibleSignal: true);
        var stopError = new InvalidOperationException("simulated stop failure");
        var captureService = new FakeAudioCaptureService(audio, stopError: stopError);
        var diagnostic = new WasapiLoopbackDiagnostic(CompleteDelayAsync);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostic.VerifyAsync(
            captureService,
            TimeSpan.FromMilliseconds(100),
            requireAudibleSignal: false));

        // Assert
        Assert.Same(stopError, exception);
        Assert.Equal(1, captureService.StartCount);
        Assert.Equal(2, captureService.StopCount);
        Assert.Equal(1, captureService.AsyncDisposeCount);
        Assert.False(captureService.IsCapturing);
        AssertAudioWasCleared(audio);
    }

    [Fact]
    public async Task VerifyAsync_StopFailsAfterDeactivatingCapture_PreservesOriginalFailureAndDisposesOnce()
    {
        // Arrange
        var audio = CreateNormalizedAudio(containsAudibleSignal: true);
        var stopError = new InvalidOperationException("primary stop failure");
        var cleanupError = new InvalidOperationException("cleanup stop failure");
        var captureService = new FakeAudioCaptureService(
            audio,
            stopError: stopError,
            stopFailureStopsCapture: true,
            cleanupStopError: cleanupError);
        var diagnostic = new WasapiLoopbackDiagnostic(CompleteDelayAsync);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostic.VerifyAsync(
            captureService,
            TimeSpan.FromMilliseconds(100),
            requireAudibleSignal: false));

        // Assert
        Assert.Same(stopError, exception);
        Assert.Equal(2, captureService.StopCount);
        Assert.Equal(1, captureService.AsyncDisposeCount);
        Assert.False(captureService.IsCapturing);
    }

    private static Task CompleteDelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static CapturedAudio CreateNormalizedAudio(
        bool containsAudibleSignal,
        TimeSpan? duration = null,
        int sampleByteCount = 3_200)
    {
        var wavData = new byte[44 + sampleByteCount];
        "RIFF"u8.CopyTo(wavData);
        BinaryPrimitives.WriteUInt32LittleEndian(wavData.AsSpan(4, 4), (uint)(wavData.Length - 8));
        "WAVE"u8.CopyTo(wavData.AsSpan(8));
        "fmt "u8.CopyTo(wavData.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wavData.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wavData.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wavData.AsSpan(22, 2), CapturedAudio.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wavData.AsSpan(24, 4), CapturedAudio.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wavData.AsSpan(28, 4), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(wavData.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wavData.AsSpan(34, 2), CapturedAudio.BitsPerSample);
        "data"u8.CopyTo(wavData.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wavData.AsSpan(40, 4), (uint)(wavData.Length - 44));

        if (containsAudibleSignal)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wavData.AsSpan(44, 2), 8_000);
        }

        return new CapturedAudio(
            wavData,
            duration ?? TimeSpan.FromMilliseconds(100),
            containsAudibleSignal);
    }

    private static void AssertAudioWasCleared(CapturedAudio audio)
    {
        Assert.All(audio.WavData.ToArray(), static value => Assert.Equal(0, value));
    }

    private sealed class FakeAudioCaptureService(
        CapturedAudio audio,
        Exception? startError = null,
        bool keepCapturingWhenStartFails = false,
        Exception? stopError = null,
        bool stopFailureStopsCapture = false,
        Exception? cleanupStopError = null) : IAudioCaptureService, IAsyncDisposable
    {
        public int AsyncDisposeCount { get; private set; }

        public bool IsCapturing { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (keepCapturingWhenStartFails)
            {
                IsCapturing = true;
            }

            if (startError is not null)
            {
                return Task.FromException(startError);
            }

            IsCapturing = true;
            return Task.CompletedTask;
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (StopCount == 1 && stopError is not null)
            {
                if (stopFailureStopsCapture)
                {
                    IsCapturing = false;
                }

                return Task.FromException<CapturedAudio>(stopError);
            }

            if (StopCount > 1 && cleanupStopError is not null)
            {
                return Task.FromException<CapturedAudio>(cleanupStopError);
            }

            cancellationToken.ThrowIfCancellationRequested();
            IsCapturing = false;
            return Task.FromResult(audio);
        }

        public ValueTask DisposeAsync()
        {
            AsyncDisposeCount++;
            IsCapturing = false;
            return ValueTask.CompletedTask;
        }
    }
}
