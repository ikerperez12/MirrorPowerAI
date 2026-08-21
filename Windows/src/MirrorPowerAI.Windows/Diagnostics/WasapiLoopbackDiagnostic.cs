using System.Buffers.Binary;
using MirrorPowerAI.Core.Audio;

namespace MirrorPowerAI.Windows.Diagnostics;

/// <summary>
/// Performs a bounded, in-memory diagnostic of the WASAPI loopback path.
/// </summary>
/// <remarks>
/// The diagnostic intentionally returns metadata only. It neither persists nor exposes captured audio.
/// </remarks>
public sealed class WasapiLoopbackDiagnostic
{
    /// <summary>
    /// Gets the default time spent collecting loopback samples.
    /// </summary>
    public static readonly TimeSpan DefaultCaptureDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets the minimum normalized sample duration that demonstrates packet delivery.
    /// </summary>
    public static readonly TimeSpan MinimumSampleDuration = TimeSpan.FromMilliseconds(100);

    private const int MinimumNormalizedDataBytes =
        (CapturedAudio.SampleRate * CapturedAudio.Channels * (CapturedAudio.BitsPerSample / 8)) / 10;
    private static readonly TimeSpan StopCleanupTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    /// <summary>
    /// Initializes a diagnostic with the production delay implementation.
    /// </summary>
    public WasapiLoopbackDiagnostic()
        : this(static (duration, cancellationToken) => Task.Delay(duration, cancellationToken))
    {
    }

    /// <summary>
    /// Initializes a diagnostic with an injectable delay for deterministic tests.
    /// </summary>
    /// <param name="delayAsync">Bounded wait used while loopback samples are collected.</param>
    public WasapiLoopbackDiagnostic(Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(delayAsync);
        _delayAsync = delayAsync;
    }

    /// <summary>
    /// Starts and stops a loopback capture, validates its normalized WAV shape, and clears all buffers.
    /// </summary>
    /// <param name="captureService">The loopback capture implementation to exercise.</param>
    /// <param name="captureDuration">The bounded time to collect samples.</param>
    /// <param name="requireAudibleSignal">
    /// Whether the diagnostic should fail when the normalized capture contains only silence.
    /// </param>
    /// <param name="cancellationToken">Cancels the collection period after native cleanup has completed.</param>
    /// <returns>A result containing only the verified audible-signal status.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="captureDuration"/> is not positive or exceeds the safe diagnostic bound.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the capture does not deliver a valid normalized WAV payload.
    /// </exception>
    public async Task<WasapiLoopbackDiagnosticResult> VerifyAsync(
        IAudioCaptureService captureService,
        TimeSpan captureDuration,
        bool requireAudibleSignal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureService);
        if (captureDuration <= TimeSpan.Zero || captureDuration > DefaultCaptureDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(captureDuration));
        }

        var captureStarted = false;
        CapturedAudio? capturedAudio = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(captureDuration + StopCleanupTimeout);
        var operationToken = operationCancellation.Token;
        try
        {
            await captureService.StartAsync(operationToken).ConfigureAwait(false);
            captureStarted = true;
            await _delayAsync(captureDuration, operationToken).ConfigureAwait(false);

            capturedAudio = await captureService.StopAsync(operationToken).ConfigureAwait(false);
            captureStarted = false;
            EnsureNormalizedWave(capturedAudio);

            var isSuccessful = !requireAudibleSignal || capturedAudio.ContainsAudibleSignal;
            return new WasapiLoopbackDiagnosticResult(isSuccessful, capturedAudio.ContainsAudibleSignal);
        }
        finally
        {
            capturedAudio?.Dispose();

            if (captureStarted || captureService.IsCapturing)
            {
                using var cleanupCancellation = new CancellationTokenSource(StopCleanupTimeout);
                try
                {
                    using var ignoredAudio = await captureService.StopAsync(cleanupCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Preserve the original diagnostic failure after making a best-effort bounded cleanup attempt.
                }
            }

            await DisposeCaptureAsync(captureService).ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeCaptureAsync(IAudioCaptureService captureService)
    {
        switch (captureService)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private static void EnsureNormalizedWave(CapturedAudio capturedAudio)
    {
        ArgumentNullException.ThrowIfNull(capturedAudio);

        var waveData = capturedAudio.WavData.Span;
        if (capturedAudio.Duration < MinimumSampleDuration
            || waveData.Length < 44 + MinimumNormalizedDataBytes
            || !waveData[..4].SequenceEqual("RIFF"u8)
            || !waveData[8..12].SequenceEqual("WAVE"u8)
            || !waveData[12..16].SequenceEqual("fmt "u8)
            || !waveData[36..40].SequenceEqual("data"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(waveData[16..20]) != 16
            || BinaryPrimitives.ReadUInt16LittleEndian(waveData[20..22]) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(waveData[22..24]) != CapturedAudio.Channels
            || BinaryPrimitives.ReadUInt32LittleEndian(waveData[24..28]) != CapturedAudio.SampleRate
            || BinaryPrimitives.ReadUInt16LittleEndian(waveData[32..34]) != 2
            || BinaryPrimitives.ReadUInt16LittleEndian(waveData[34..36]) != CapturedAudio.BitsPerSample
            || BinaryPrimitives.ReadUInt32LittleEndian(waveData[40..44])
                != (uint)(waveData.Length - 44)
            || BinaryPrimitives.ReadUInt32LittleEndian(waveData[40..44]) < MinimumNormalizedDataBytes)
        {
            throw new InvalidOperationException("The loopback diagnostic did not receive a valid normalized WAV payload.");
        }
    }
}

/// <summary>
/// Contains only the non-sensitive outcome of a WASAPI loopback diagnostic.
/// </summary>
/// <param name="IsSuccessful">Whether the configured diagnostic condition was satisfied.</param>
/// <param name="ContainsAudibleSignal">Whether the normalized capture contained an audible signal.</param>
public sealed record WasapiLoopbackDiagnosticResult(bool IsSuccessful, bool ContainsAudibleSignal);
