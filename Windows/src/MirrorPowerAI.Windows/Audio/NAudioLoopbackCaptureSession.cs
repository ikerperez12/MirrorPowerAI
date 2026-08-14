using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Creates defensive shared-mode WASAPI loopback sessions backed by NAudio 2.x Core Audio APIs.
/// </summary>
public sealed class NAudioLoopbackCaptureSessionFactory : ILoopbackCaptureSessionFactory
{
    /// <inheritdoc />
    public ILoopbackCaptureSession Create(AudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new NAudioLoopbackCaptureSession(endpoint.Id);
    }
}

/// <summary>
/// Captures a render endpoint in WASAPI shared loopback mode and contains native teardown failures.
/// </summary>
public sealed class NAudioLoopbackCaptureSession : ILoopbackCaptureSession
{
    private const long ReferenceTimesPerSecond = 10_000_000;
    private const long ReferenceTimesPerMillisecond = 10_000;
    private const int RequestedBufferMilliseconds = 100;

    private readonly MMDevice _device;
    private readonly AudioClient _audioClient;
    private readonly WaveFormat _waveFormat;
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private Thread? _captureThread;
    private int _started;
    private int _disposed;

    /// <summary>
    /// Initializes a capture session for an existing render endpoint identifier.
    /// </summary>
    /// <param name="deviceId">Windows render endpoint identifier.</param>
    public NAudioLoopbackCaptureSession(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(deviceId);
        if (_device.State != DeviceState.Active)
        {
            _device.Dispose();
            throw new AudioCaptureException(
                AudioCaptureFailure.DeviceUnavailable,
                "The selected audio output device is not active.");
        }

        AudioClient? audioClient = null;
        try
        {
            audioClient = _device.AudioClient;
            _audioClient = audioClient;
            _waveFormat = _audioClient.MixFormat;
            SourceFormat = ToAudioSampleFormat(_waveFormat);

            _audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback
                    | AudioClientStreamFlags.AutoConvertPcm
                    | AudioClientStreamFlags.SrcDefaultQuality,
                RequestedBufferMilliseconds * ReferenceTimesPerMillisecond,
                0,
                _waveFormat,
                Guid.Empty);
        }
        catch
        {
            audioClient?.Dispose();
            _device.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public event EventHandler<LoopbackAudioDataEventArgs>? DataAvailable;

    /// <inheritdoc />
    public event EventHandler<LoopbackCaptureStoppedEventArgs>? Stopped;

    /// <inheritdoc />
    public AudioSampleFormat SourceFormat { get; }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The loopback session can only be started once.");
        }

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "MirrorPowerAI WASAPI loopback",
        };
        _captureThread.Start();
    }

    /// <inheritdoc />
    public void RequestStop() => _stopRequested.Set();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopRequested.Set();
        var thread = _captureThread;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                // Do not release COM resources underneath a native call that failed to return.
                return;
            }
        }

        // The client owns its AudioCaptureClient wrapper. Dispose it before the endpoint.
        _audioClient.Dispose();
        _device.Dispose();
        _stopRequested.Dispose();
    }

    private void CaptureLoop()
    {
        Exception? failure = null;
        var clientStarted = false;

        try
        {
            var captureClient = _audioClient.AudioCaptureClient;
            var bufferFrameCount = _audioClient.BufferSize;
            var actualDuration = (long)(ReferenceTimesPerSecond
                * (double)bufferFrameCount
                / _waveFormat.SampleRate);
            var sleepMilliseconds = Math.Max(
                1,
                (int)(actualDuration / ReferenceTimesPerMillisecond / 2));

            _audioClient.Start();
            clientStarted = true;

            while (!_stopRequested.Wait(sleepMilliseconds))
            {
                ReadAvailablePackets(captureClient);
            }

            // Drain the last complete packets before stopping.
            ReadAvailablePackets(captureClient);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (clientStarted)
            {
                try
                {
                    _audioClient.Stop();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            Stopped?.Invoke(this, new LoopbackCaptureStoppedEventArgs(failure));
        }
    }

    private void ReadAvailablePackets(AudioCaptureClient captureClient)
    {
        while (captureClient.GetNextPacketSize() > 0)
        {
            var buffer = captureClient.GetBuffer(out var framesAvailable, out var flags);
            try
            {
                var byteCount = checked(framesAvailable * SourceFormat.BlockAlign);
                var copied = new byte[byteCount];
                if ((flags & AudioClientBufferFlags.Silent) != AudioClientBufferFlags.Silent
                    && byteCount > 0)
                {
                    Marshal.Copy(buffer, copied, 0, byteCount);
                }

                try
                {
                    DataAvailable?.Invoke(this, new LoopbackAudioDataEventArgs(copied));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(copied);
                }
            }
            finally
            {
                captureClient.ReleaseBuffer(framesAvailable);
            }
        }
    }

    private static AudioSampleFormat ToAudioSampleFormat(WaveFormat format)
    {
        var standardFormat = format is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : format;
        var encoding = standardFormat.Encoding switch
        {
            WaveFormatEncoding.Pcm => AudioSampleEncoding.PcmInteger,
            WaveFormatEncoding.IeeeFloat => AudioSampleEncoding.IeeeFloat,
            _ => throw new NotSupportedException(
                $"The audio endpoint mix format '{standardFormat.Encoding}' is unsupported."),
        };

        return new AudioSampleFormat(
            standardFormat.SampleRate,
            standardFormat.Channels,
            standardFormat.BitsPerSample,
            encoding);
    }
}
