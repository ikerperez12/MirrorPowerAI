namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Carries an ephemeral, frame-aligned chunk produced by a loopback session.
/// Consumers must process it synchronously and must not retain the buffer.
/// </summary>
public sealed class LoopbackAudioDataEventArgs : EventArgs
{
    /// <summary>
    /// Initializes event data for a captured chunk.
    /// </summary>
    /// <param name="buffer">Copied chunk bytes.</param>
    public LoopbackAudioDataEventArgs(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Buffer = buffer;
    }

    /// <summary>Gets the ephemeral chunk bytes, which are zeroed after synchronous delivery.</summary>
    public byte[] Buffer { get; }
}

/// <summary>
/// Reports completion of a low-level loopback session.
/// </summary>
public sealed class LoopbackCaptureStoppedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes completion event data.
    /// </summary>
    /// <param name="exception">The capture error, or <see langword="null"/> after an orderly stop.</param>
    public LoopbackCaptureStoppedEventArgs(Exception? exception) => Exception = exception;

    /// <summary>Gets the error that ended capture, if any.</summary>
    public Exception? Exception { get; }
}

/// <summary>
/// Abstracts one shared-mode WASAPI loopback session for deterministic service testing.
/// </summary>
public interface ILoopbackCaptureSession : IDisposable
{
    /// <summary>Occurs when a copied chunk is ready.</summary>
    event EventHandler<LoopbackAudioDataEventArgs>? DataAvailable;

    /// <summary>Occurs exactly once after capture has stopped.</summary>
    event EventHandler<LoopbackCaptureStoppedEventArgs>? Stopped;

    /// <summary>Gets the raw format emitted by this session.</summary>
    AudioSampleFormat SourceFormat { get; }

    /// <summary>Starts capture on a background thread.</summary>
    void Start();

    /// <summary>Requests an orderly stop without blocking the caller.</summary>
    void RequestStop();
}

/// <summary>
/// Creates low-level loopback sessions for a resolved endpoint.
/// </summary>
public interface ILoopbackCaptureSessionFactory
{
    /// <summary>
    /// Creates a session that owns its native endpoint resources.
    /// </summary>
    /// <param name="endpoint">Resolved render endpoint.</param>
    /// <returns>A new, stopped session.</returns>
    ILoopbackCaptureSession Create(AudioEndpoint endpoint);
}

/// <summary>
/// Provides cancellable delays to the capture watchdog.
/// </summary>
public interface ICaptureTimer
{
    /// <summary>
    /// Delays for the specified logical duration.
    /// </summary>
    /// <param name="delay">Delay duration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes after the delay.</returns>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Uses the operating-system timer for capture monitoring.
/// </summary>
public sealed class SystemCaptureTimer : ICaptureTimer
{
    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
