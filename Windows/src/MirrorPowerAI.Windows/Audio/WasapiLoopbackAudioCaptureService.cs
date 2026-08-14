using System.IO;
using System.Security.Cryptography;
using MirrorPowerAI.Core.Audio;

namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Coordinates bounded, in-memory WASAPI loopback capture and normalization.
/// </summary>
public sealed class WasapiLoopbackAudioCaptureService : IAudioCaptureService, IAsyncDisposable
{
    /// <summary>The hard v1 capture-duration ceiling.</summary>
    public static readonly TimeSpan AbsoluteMaximumDuration = TimeSpan.FromMinutes(5);

    private const long DefaultMaximumRawBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan DefaultEndpointPollInterval = TimeSpan.FromSeconds(1);

    private readonly IAudioEndpointProvider _endpointProvider;
    private readonly ILoopbackCaptureSessionFactory _sessionFactory;
    private readonly Pcm16WaveConverter _converter;
    private readonly ICaptureTimer _timer;
    private readonly string? _requestedDeviceId;
    private readonly TimeSpan _maximumDuration;
    private readonly TimeSpan _endpointPollInterval;
    private readonly long _maximumRawBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CaptureContext? _activeCapture;
    private int _disposed;

    /// <summary>
    /// Initializes a service using the system default render endpoint and production adapters.
    /// </summary>
    public WasapiLoopbackAudioCaptureService()
        : this(
            new NAudioEndpointProvider(),
            new NAudioLoopbackCaptureSessionFactory(),
            new Pcm16WaveConverter(),
            new SystemCaptureTimer(),
            null,
            AbsoluteMaximumDuration,
            DefaultEndpointPollInterval,
            DefaultMaximumRawBytes)
    {
    }

    /// <summary>
    /// Initializes a service with explicit adapters and limits.
    /// </summary>
    /// <param name="endpointProvider">Render endpoint resolver and monitor.</param>
    /// <param name="sessionFactory">Low-level loopback-session factory.</param>
    /// <param name="converter">In-memory normalization component.</param>
    /// <param name="timer">Cancellable watchdog timer.</param>
    /// <param name="requestedDeviceId">Specific endpoint identifier, or <see langword="null"/> for the default.</param>
    /// <param name="maximumDuration">Capture duration, capped at five minutes.</param>
    /// <param name="endpointPollInterval">Endpoint health polling interval.</param>
    /// <param name="maximumRawBytes">Additional hard bound for unusual high-bandwidth endpoint formats.</param>
    public WasapiLoopbackAudioCaptureService(
        IAudioEndpointProvider endpointProvider,
        ILoopbackCaptureSessionFactory sessionFactory,
        Pcm16WaveConverter converter,
        ICaptureTimer timer,
        string? requestedDeviceId = null,
        TimeSpan? maximumDuration = null,
        TimeSpan? endpointPollInterval = null,
        long maximumRawBytes = DefaultMaximumRawBytes)
    {
        ArgumentNullException.ThrowIfNull(endpointProvider);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(timer);

        var effectiveDuration = maximumDuration ?? AbsoluteMaximumDuration;
        if (effectiveDuration <= TimeSpan.Zero || effectiveDuration > AbsoluteMaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        }

        var effectivePollInterval = endpointPollInterval ?? DefaultEndpointPollInterval;
        if (effectivePollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointPollInterval));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRawBytes, 1);

        _endpointProvider = endpointProvider;
        _sessionFactory = sessionFactory;
        _converter = converter;
        _timer = timer;
        _requestedDeviceId = string.IsNullOrWhiteSpace(requestedDeviceId)
            ? null
            : requestedDeviceId;
        _maximumDuration = effectiveDuration;
        _endpointPollInterval = effectivePollInterval;
        _maximumRawBytes = maximumRawBytes;
    }

    /// <inheritdoc />
    public bool IsCapturing => Volatile.Read(ref _activeCapture) is not null;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeCapture is not null)
            {
                throw new InvalidOperationException("An audio capture session is already active.");
            }

            var endpoint = _endpointProvider.GetRenderEndpoint(_requestedDeviceId);
            var session = _sessionFactory.Create(endpoint);
            CaptureContext? context = null;

            try
            {
                var durationBound = checked((long)Math.Ceiling(
                    session.SourceFormat.AverageBytesPerSecond * _maximumDuration.TotalSeconds));
                var rawByteLimit = Math.Min(_maximumRawBytes, durationBound + session.SourceFormat.BlockAlign);
                context = new CaptureContext(endpoint, session, rawByteLimit);
                session.DataAvailable += context.OnDataAvailable;
                session.Stopped += context.OnStopped;

                Volatile.Write(ref _activeCapture, context);
                session.Start();
                context.WatchdogTask = WatchCaptureAsync(context);
            }
            catch
            {
                if (context is not null)
                {
                    session.DataAvailable -= context.OnDataAvailable;
                    session.Stopped -= context.OnStopped;
                    context.Dispose();
                }
                else
                {
                    session.Dispose();
                }

                Volatile.Write(ref _activeCapture, null);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CaptureContext context;

        // Once StopAsync is called, native teardown must finish even if the caller cancels.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            context = _activeCapture
                ?? throw new InvalidOperationException("No audio capture session is active.");
            context.TrySetStopReason(CaptureStopReason.Requested);
            context.Session.RequestStop();
        }
        finally
        {
            _gate.Release();
        }

        var sourceFormat = context.Session.SourceFormat;
        Exception? backendError = null;
        var callerCancelled = false;
        byte[]? rawAudio = null;
        var stopReason = CaptureStopReason.None;
        try
        {
            try
            {
                backendError = await context.Stopped.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                callerCancelled = true;
                try
                {
                    backendError = await context.Stopped.Task
                        .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    backendError = exception;
                }
            }

            stopReason = context.StopReason;
            var canReturnAudio = !callerCancelled
                && backendError is null
                && stopReason is CaptureStopReason.Requested or CaptureStopReason.MaximumDuration;
            rawAudio = canReturnAudio ? context.GetCapturedBytes() : null;
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_activeCapture, context))
                {
                    Volatile.Write(ref _activeCapture, null);
                }
            }
            finally
            {
                _gate.Release();
            }

            await StopWatchdogAndDisposeAsync(context).ConfigureAwait(false);
        }

        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        ThrowForAbnormalStop(stopReason);

        if (backendError is not null)
        {
            throw new AudioCaptureException(
                AudioCaptureFailure.BackendFailure,
                "Windows stopped the loopback audio session unexpectedly.",
                backendError);
        }

        try
        {
            var normalized = _converter.Convert(rawAudio!, sourceFormat);
            try
            {
                return new CapturedAudio(
                    normalized.WavData,
                    normalized.Duration,
                    normalized.ContainsAudibleSignal);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(normalized.WavData);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawAudio!);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        CaptureContext? context;
        try
        {
            context = _activeCapture;
            Volatile.Write(ref _activeCapture, null);
            context?.Session.RequestStop();
        }
        finally
        {
            _gate.Release();
        }

        if (context is not null)
        {
            try
            {
                _ = await context.Stopped.Task
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Disposal below performs a final bounded native-session stop.
            }

            await StopWatchdogAndDisposeAsync(context).ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task WatchCaptureAsync(CaptureContext context)
    {
        var elapsed = TimeSpan.Zero;
        try
        {
            while (elapsed < _maximumDuration)
            {
                var remaining = _maximumDuration - elapsed;
                var delay = remaining < _endpointPollInterval
                    ? remaining
                    : _endpointPollInterval;
                await _timer.DelayAsync(delay, context.WatchdogCancellation.Token)
                    .ConfigureAwait(false);
                elapsed += delay;

                if (!_endpointProvider.IsEndpointActive(context.Endpoint.Id))
                {
                    context.TrySetStopReason(CaptureStopReason.DeviceDisconnected);
                    context.Session.RequestStop();
                    return;
                }

                if (context.Endpoint.WasSelectedAsDefault
                    && !_endpointProvider.IsDefaultRenderEndpoint(context.Endpoint.Id))
                {
                    context.TrySetStopReason(CaptureStopReason.DefaultDeviceChanged);
                    context.Session.RequestStop();
                    return;
                }
            }

            context.TrySetStopReason(CaptureStopReason.MaximumDuration);
            context.Session.RequestStop();
        }
        catch (OperationCanceledException) when (context.WatchdogCancellation.IsCancellationRequested)
        {
            // Normal shutdown cancels the watchdog after the low-level session has stopped.
        }
        catch (Exception exception)
        {
            context.TrySetWatchdogFailure(exception);
            context.Session.RequestStop();
        }
    }

    private static async Task StopWatchdogAndDisposeAsync(CaptureContext context)
    {
        context.WatchdogCancellation.Cancel();
        if (context.WatchdogTask is not null)
        {
            await context.WatchdogTask.ConfigureAwait(false);
        }

        context.Session.DataAvailable -= context.OnDataAvailable;
        context.Session.Stopped -= context.OnStopped;
        context.Dispose();
    }

    private static void ThrowForAbnormalStop(CaptureStopReason reason)
    {
        switch (reason)
        {
            case CaptureStopReason.Requested:
            case CaptureStopReason.MaximumDuration:
                return;
            case CaptureStopReason.DeviceDisconnected:
                throw new AudioCaptureException(
                    AudioCaptureFailure.DeviceDisconnected,
                    "The audio output device was disconnected during capture.");
            case CaptureStopReason.DefaultDeviceChanged:
                throw new AudioCaptureException(
                    AudioCaptureFailure.DefaultDeviceChanged,
                    "The default audio output device changed during capture.");
            case CaptureStopReason.BufferLimit:
                throw new AudioCaptureException(
                    AudioCaptureFailure.BufferLimitReached,
                    "The bounded in-memory audio buffer reached its safety limit.");
            case CaptureStopReason.WatchdogFailure:
                throw new AudioCaptureException(
                    AudioCaptureFailure.BackendFailure,
                    "The audio-device watchdog failed.");
            default:
                throw new AudioCaptureException(
                    AudioCaptureFailure.BackendFailure,
                    "The loopback session stopped without a completion reason.");
        }
    }

    private enum CaptureStopReason
    {
        None,
        Requested,
        MaximumDuration,
        DeviceDisconnected,
        DefaultDeviceChanged,
        BufferLimit,
        WatchdogFailure,
    }

    private sealed class CaptureContext : IDisposable
    {
        private readonly MemoryStream _rawAudio = new();
        private readonly object _bufferSync = new();
        private readonly long _rawByteLimit;
        private int _stopReason;

        public CaptureContext(
            AudioEndpoint endpoint,
            ILoopbackCaptureSession session,
            long rawByteLimit)
        {
            Endpoint = endpoint;
            Session = session;
            _rawByteLimit = rawByteLimit;
        }

        public AudioEndpoint Endpoint { get; }

        public ILoopbackCaptureSession Session { get; }

        public TaskCompletionSource<Exception?> Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource WatchdogCancellation { get; } = new();

        public Task? WatchdogTask { get; set; }

        public CaptureStopReason StopReason => (CaptureStopReason)Volatile.Read(ref _stopReason);

        public void OnDataAvailable(object? sender, LoopbackAudioDataEventArgs eventArgs)
        {
            ArgumentNullException.ThrowIfNull(eventArgs);
            var shouldStop = false;
            try
            {
                lock (_bufferSync)
                {
                    var remaining = _rawByteLimit - _rawAudio.Length;
                    var writable = (int)Math.Min(Math.Max(0, remaining), eventArgs.Buffer.Length);
                    writable -= writable % Session.SourceFormat.BlockAlign;
                    if (writable > 0)
                    {
                        _rawAudio.Write(eventArgs.Buffer, 0, writable);
                    }

                    shouldStop = writable < eventArgs.Buffer.Length;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(eventArgs.Buffer);
            }

            if (shouldStop)
            {
                TrySetStopReason(CaptureStopReason.BufferLimit);
                Session.RequestStop();
            }
        }

        public void OnStopped(object? sender, LoopbackCaptureStoppedEventArgs eventArgs)
        {
            ArgumentNullException.ThrowIfNull(eventArgs);
            Stopped.TrySetResult(eventArgs.Exception);
        }

        public byte[] GetCapturedBytes()
        {
            lock (_bufferSync)
            {
                return _rawAudio.ToArray();
            }
        }

        public bool TrySetStopReason(CaptureStopReason reason) =>
            Interlocked.CompareExchange(ref _stopReason, (int)reason, (int)CaptureStopReason.None)
                == (int)CaptureStopReason.None;

        public void TrySetWatchdogFailure(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _ = TrySetStopReason(CaptureStopReason.WatchdogFailure);
        }

        public void Dispose()
        {
            WatchdogCancellation.Dispose();
            Session.Dispose();
            lock (_bufferSync)
            {
                if (_rawAudio.TryGetBuffer(out var buffer))
                {
                    CryptographicOperations.ZeroMemory(buffer.AsSpan());
                }
            }

            _rawAudio.Dispose();
        }
    }
}
