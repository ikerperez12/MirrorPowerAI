using MirrorPowerAI.Windows.Audio;

namespace MirrorPowerAI.Windows.Tests.Audio;

public sealed class WasapiLoopbackAudioCaptureServiceTests
{
    private static readonly AudioSampleFormat TestFormat =
        new(16_000, 1, 16, AudioSampleEncoding.PcmInteger);

    [Fact]
    public async Task StopAsync_SilentCapture_ReturnsDetectedSilence()
    {
        // Arrange
        var session = new FakeLoopbackCaptureSession(TestFormat, new byte[3_200]);
        await using var service = CreateService(session);

        // Act
        await service.StartAsync();
        var result = await service.StopAsync();

        // Assert
        Assert.False(result.ContainsAudibleSignal);
        Assert.False(service.IsCapturing);
        Assert.Equal(16_000, ReadWaveSampleRate(result.WavData.Span));
    }

    [Fact]
    public async Task StartAsync_WhileCapturing_ThrowsInvalidOperationException()
    {
        // Arrange
        var session = new FakeLoopbackCaptureSession(TestFormat, new byte[3_200]);
        await using var service = CreateService(session);
        await service.StartAsync();

        // Act and assert
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());
        _ = await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_MaximumDurationReached_ReturnsBoundedCapture()
    {
        // Arrange
        var session = new FakeLoopbackCaptureSession(TestFormat, AudiblePcm16(1_600));
        var clock = new ManualTimeProvider();
        await using var service = CreateService(
            session,
            timer: new ImmediateCaptureTimer(clock),
            maximumDuration: TimeSpan.FromSeconds(2),
            timeProvider: clock);

        // Act
        await service.StartAsync();
        var result = await service.StopAsync();

        // Assert
        Assert.True(result.ContainsAudibleSignal);
        Assert.True(session.StopRequestCount >= 1);
    }

    [Fact]
    public async Task StopAsync_DefaultDeviceChanges_ThrowsCategorizedFailure()
    {
        // Arrange
        var endpointProvider = new FakeEndpointProvider { IsDefault = false };
        var session = new FakeLoopbackCaptureSession(TestFormat, AudiblePcm16(1_600));
        var clock = new ManualTimeProvider();
        await using var service = CreateService(
            session,
            endpointProvider,
            new ImmediateCaptureTimer(clock),
            TimeSpan.FromSeconds(2),
            timeProvider: clock);

        // Act
        await service.StartAsync();
        var exception = await Assert.ThrowsAsync<AudioCaptureException>(() => service.StopAsync());

        // Assert
        Assert.Equal(AudioCaptureFailure.DefaultDeviceChanged, exception.Failure);
    }

    [Fact]
    public async Task StopAsync_DeviceDisconnects_ThrowsCategorizedFailure()
    {
        // Arrange
        var endpointProvider = new FakeEndpointProvider { Active = false };
        var session = new FakeLoopbackCaptureSession(TestFormat, AudiblePcm16(1_600));
        var clock = new ManualTimeProvider();
        await using var service = CreateService(
            session,
            endpointProvider,
            new ImmediateCaptureTimer(clock),
            TimeSpan.FromSeconds(2),
            timeProvider: clock);

        // Act
        await service.StartAsync();
        var exception = await Assert.ThrowsAsync<AudioCaptureException>(() => service.StopAsync());

        // Assert
        Assert.Equal(AudioCaptureFailure.DeviceDisconnected, exception.Failure);
    }

    [Fact]
    public async Task StopAsync_BufferLimitReached_ThrowsCategorizedFailure()
    {
        // Arrange
        var session = new FakeLoopbackCaptureSession(TestFormat, AudiblePcm16(1_600));
        await using var service = CreateService(session, maximumRawBytes: 100);

        // Act
        await service.StartAsync();
        var exception = await Assert.ThrowsAsync<AudioCaptureException>(() => service.StopAsync());

        // Assert
        Assert.Equal(AudioCaptureFailure.BufferLimitReached, exception.Failure);
    }

    [Fact]
    public async Task StopAsync_BackendFails_ThrowsBackendFailureWithoutNativeMessageInPublicText()
    {
        // Arrange
        var session = new FakeLoopbackCaptureSession(
            TestFormat,
            Array.Empty<byte>(),
            new InvalidOperationException("sensitive native device id"));
        await using var service = CreateService(session);

        // Act
        await service.StartAsync();
        var exception = await Assert.ThrowsAsync<AudioCaptureException>(() => service.StopAsync());

        // Assert
        Assert.Equal(AudioCaptureFailure.BackendFailure, exception.Failure);
        Assert.DoesNotContain("sensitive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopAsync_CallerCancelsDuringStop_StillCleansSessionAndEphemeralBuffer()
    {
        // Arrange
        var deliveredBuffer = AudiblePcm16(1_600);
        var session = new FakeLoopbackCaptureSession(
            TestFormat,
            deliveredBuffer,
            stopCompletionDelay: TimeSpan.FromMilliseconds(25));
        await using var service = CreateService(session);
        await service.StartAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.StopAsync(cancellation.Token));

        // Assert
        Assert.False(service.IsCapturing);
        Assert.True(session.WasDisposed);
        Assert.All(deliveredBuffer, static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task StopAsync_BackendNeverSignalsStop_TimesOutAndReleasesSession()
    {
        // Arrange
        var session = new FakeLoopbackCaptureSession(
            TestFormat,
            AudiblePcm16(1_600),
            suppressStopCompletion: true);
        await using var service = CreateService(
            session,
            stopCompletionTimeout: TimeSpan.FromMilliseconds(10));
        await service.StartAsync();

        // Act
        var exception = await Assert.ThrowsAsync<AudioCaptureException>(() => service.StopAsync());

        // Assert
        Assert.Equal(AudioCaptureFailure.BackendFailure, exception.Failure);
        Assert.False(service.IsCapturing);
        Assert.True(session.WasDisposed);
    }

    [Fact]
    public void Constructor_DurationOverFiveMinutes_RejectsConfiguration()
    {
        // Arrange
        var session = new FakeLoopbackCaptureSession(TestFormat, Array.Empty<byte>());

        // Act and assert
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(
            session,
            maximumDuration: TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1)));
    }

    private static WasapiLoopbackAudioCaptureService CreateService(
        FakeLoopbackCaptureSession session,
        FakeEndpointProvider? endpointProvider = null,
        ICaptureTimer? timer = null,
        TimeSpan? maximumDuration = null,
        long maximumRawBytes = 1024 * 1024,
        TimeSpan? stopCompletionTimeout = null,
        TimeProvider? timeProvider = null) =>
        new(
            endpointProvider ?? new FakeEndpointProvider(),
            new FakeLoopbackCaptureSessionFactory(session),
            new Pcm16WaveConverter(),
            timer ?? new NeverCompletingCaptureTimer(),
            requestedDeviceId: null,
            maximumDuration: maximumDuration ?? TimeSpan.FromMinutes(5),
            endpointPollInterval: TimeSpan.FromSeconds(1),
            maximumRawBytes: maximumRawBytes,
            stopCompletionTimeout: stopCompletionTimeout,
            timeProvider: timeProvider);

    private static int ReadWaveSampleRate(ReadOnlySpan<byte> wave) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(wave[24..28]);

    private static byte[] AudiblePcm16(int sampleCount)
    {
        var bytes = new byte[sampleCount * sizeof(short)];
        for (var index = 0; index < sampleCount; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(index * sizeof(short), sizeof(short)),
                index % 2 == 0 ? (short)8_000 : (short)-8_000);
        }

        return bytes;
    }

    private sealed class FakeEndpointProvider : IAudioEndpointProvider
    {
        public bool Active { get; set; } = true;

        public bool IsDefault { get; set; } = true;

        public IReadOnlyList<AudioEndpoint> GetActiveRenderEndpoints() =>
            [new AudioEndpoint("endpoint-1", "Test endpoint", true)];

        public AudioEndpoint GetRenderEndpoint(string? deviceId) =>
            new("endpoint-1", "Test endpoint", WasSelectedAsDefault: deviceId is null);

        public bool IsEndpointActive(string deviceId) => Active;

        public bool IsDefaultRenderEndpoint(string deviceId) => IsDefault;
    }

    private sealed class FakeLoopbackCaptureSessionFactory(FakeLoopbackCaptureSession session)
        : ILoopbackCaptureSessionFactory
    {
        public ILoopbackCaptureSession Create(AudioEndpoint endpoint) => session;
    }

    private sealed class FakeLoopbackCaptureSession(
        AudioSampleFormat sourceFormat,
        byte[] initialData,
        Exception? stopError = null,
        TimeSpan? stopCompletionDelay = null,
        bool suppressStopCompletion = false) : ILoopbackCaptureSession
    {
        private int _stopped;

        public event EventHandler<LoopbackAudioDataEventArgs>? DataAvailable;

        public event EventHandler<LoopbackCaptureStoppedEventArgs>? Stopped;

        public AudioSampleFormat SourceFormat { get; } = sourceFormat;

        public int StopRequestCount { get; private set; }

        public bool WasDisposed { get; private set; }

        public void Start()
        {
            if (initialData.Length > 0)
            {
                DataAvailable?.Invoke(this, new LoopbackAudioDataEventArgs(initialData));
            }
        }

        public void RequestStop()
        {
            StopRequestCount++;
            if (suppressStopCompletion)
            {
                return;
            }

            if (stopCompletionDelay is { } delay && delay > TimeSpan.Zero)
            {
                _ = CompleteAfterDelayAsync(delay);
                return;
            }

            CompleteStop();
        }

        public void Dispose()
        {
            WasDisposed = true;
        }

        private async Task CompleteAfterDelayAsync(TimeSpan delay)
        {
            await Task.Delay(delay);
            CompleteStop();
        }

        private void CompleteStop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                Stopped?.Invoke(this, new LoopbackCaptureStoppedEventArgs(stopError));
            }
        }
    }

    private sealed class ImmediateCaptureTimer(ManualTimeProvider timeProvider) : ICaptureTimer
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeProvider.Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class NeverCompletingCaptureTimer : ICaptureTimer
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
