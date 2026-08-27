using System.Buffers.Binary;
using System.IO;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Models;
using MirrorPowerAI.Core.Transcription;

namespace MirrorPowerAI.Windows.Transcription;

/// <summary>
/// Transcribes normalized audio locally with an explicitly verified Whisper model.
/// </summary>
public sealed class WhisperLocalTranscriptionService : ITranscriptionService, IDisposable
{
    private const int WaveHeaderLength = 44;
    private static readonly TimeSpan MaximumAudioDuration = TimeSpan.FromMinutes(5);
    private readonly IWhisperModelLeaseProvider _modelLeaseProvider;
    private readonly IWhisperInferenceEngine _inferenceEngine;
    private readonly IWhisperInferencePrewarmer? _prewarmer;
    private readonly string _modelDirectory;
    private readonly int _threadCount;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private IWhisperModelLease? _preparedModelLease;
    private int _disposed;

    /// <summary>
    /// Initializes the production local transcription service.
    /// </summary>
    /// <param name="modelManager">Pinned model download and verification manager.</param>
    /// <param name="modelDirectory">Application-owned model directory.</param>
    /// <param name="threadCount">Optional CPU inference thread count.</param>
    public WhisperLocalTranscriptionService(
        WhisperModelManager modelManager,
        string modelDirectory,
        int? threadCount = null)
        : this(
            new WhisperModelLeaseProvider(modelManager),
            new WhisperNetInferenceEngine(),
            modelDirectory,
            threadCount)
    {
    }

    /// <summary>
    /// Initializes a testable local transcription service with explicit adapters.
    /// </summary>
    /// <param name="modelLeaseProvider">Verified model lease provider.</param>
    /// <param name="inferenceEngine">Local inference adapter.</param>
    /// <param name="modelDirectory">Application-owned model directory.</param>
    /// <param name="threadCount">Optional CPU inference thread count.</param>
    public WhisperLocalTranscriptionService(
        IWhisperModelLeaseProvider modelLeaseProvider,
        IWhisperInferenceEngine inferenceEngine,
        string modelDirectory,
        int? threadCount = null)
    {
        ArgumentNullException.ThrowIfNull(modelLeaseProvider);
        ArgumentNullException.ThrowIfNull(inferenceEngine);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        var effectiveThreadCount = threadCount
            ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveThreadCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(effectiveThreadCount, 32);

        _modelLeaseProvider = modelLeaseProvider;
        _inferenceEngine = inferenceEngine;
        _prewarmer = inferenceEngine as IWhisperInferencePrewarmer;
        _modelDirectory = Path.GetFullPath(modelDirectory);
        _threadCount = effectiveThreadCount;
    }

    /// <inheritdoc />
    public TranscriptionProvider Provider => TranscriptionProvider.LocalWhisper;

    /// <summary>
    /// Verifies and loads the local model/runtime before capture starts.
    /// </summary>
    /// <remarks>
    /// This is a no-op for inference adapters that do not advertise the optional prewarm
    /// capability. It intentionally performs no audio processing and remains fully cancelable.
    /// </remarks>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_prewarmer is null)
        {
            return;
        }

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsurePreparedUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(
        CapturedAudio audio,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        cancellationToken.ThrowIfCancellationRequested();

        if (!audio.ContainsAudibleSignal)
        {
            throw new WhisperTranscriptionException(
                WhisperTranscriptionFailure.NoAudibleSignal,
                "No se ha detectado audio audible para transcribir.");
        }

        if (!TryGetNormalizedWaveDuration(audio.WavData.Span, out var waveDuration)
            || waveDuration > MaximumAudioDuration
            || audio.Duration != waveDuration)
        {
            throw new WhisperTranscriptionException(
                WhisperTranscriptionFailure.InvalidAudio,
                "El audio no tiene el formato PCM normalizado requerido.");
        }

        var normalizedLanguage = NormalizeLanguage(language);
        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            // Direct callers that do not use the shell's explicit preflight still receive
            // the same one-time warm-up guarantee before their first segment is processed.
            await EnsurePreparedUnderGateAsync(cancellationToken).ConfigureAwait(false);

            IWhisperModelLease? transientModelLease = null;
            if (_prewarmer is null)
            {
                transientModelLease = await _modelLeaseProvider
                    .AcquireVerifiedLeaseAsync(_modelDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }

            string transcript;
            try
            {
                transcript = await _inferenceEngine
                    .TranscribeAsync(
                        (_preparedModelLease ?? transientModelLease)?.ModelPath
                            ?? throw new InvalidOperationException("No se ha preparado el modelo Whisper."),
                        audio.WavData,
                        normalizedLanguage,
                        _threadCount,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                transientModelLease?.Dispose();
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new WhisperTranscriptionException(
                    WhisperTranscriptionFailure.EmptyTranscript,
                    "Whisper no ha producido ninguna transcripción.");
            }

            return transcript.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WhisperTranscriptionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new WhisperTranscriptionException(
                WhisperTranscriptionFailure.InferenceFailed,
                "Whisper no ha podido procesar el audio localmente.",
                exception);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /// <summary>
    /// Releases the cached inference runtime, when the selected engine owns one.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _inferenceGate.Wait();
        try
        {
            _preparedModelLease?.Dispose();
            _preparedModelLease = null;

            if (_inferenceEngine is IDisposable disposableEngine)
            {
                disposableEngine.Dispose();
            }
        }
        finally
        {
            _inferenceGate.Release();
            _inferenceGate.Dispose();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private async Task EnsurePreparedUnderGateAsync(CancellationToken cancellationToken)
    {
        if (_prewarmer is null || _preparedModelLease is not null)
        {
            return;
        }

        var modelLease = await _modelLeaseProvider
            .AcquireVerifiedLeaseAsync(_modelDirectory, cancellationToken)
            .ConfigureAwait(false);
        var keepLease = false;
        try
        {
            try
            {
                await _prewarmer
                    .PrepareAsync(modelLease.ModelPath, _threadCount, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (WhisperTranscriptionException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new WhisperTranscriptionException(
                    WhisperTranscriptionFailure.InferenceFailed,
                    "Whisper no ha podido cargar el modelo local.",
                    exception);
            }

            _preparedModelLease = modelLease;
            keepLease = true;
        }
        finally
        {
            if (!keepLease)
            {
                modelLease.Dispose();
            }
        }
    }

    private static string NormalizeLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var normalized = language.Trim().ToLowerInvariant();
        if (normalized.Length > 16
            || !normalized.All(static character =>
                character is >= 'a' and <= 'z' or '-'))
        {
            throw new ArgumentException(
                "The language must be 'auto' or a short language code.",
                nameof(language));
        }

        return normalized;
    }

    private static bool TryGetNormalizedWaveDuration(ReadOnlySpan<byte> wave, out TimeSpan duration)
    {
        duration = default;
        if (wave.Length < WaveHeaderLength
            || !wave[..4].SequenceEqual("RIFF"u8)
            || !wave[8..12].SequenceEqual("WAVE"u8)
            || !wave[12..16].SequenceEqual("fmt "u8)
            || !wave[36..40].SequenceEqual("data"u8))
        {
            return false;
        }

        var riffChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wave[4..8]);
        var formatChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wave[16..20]);
        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(wave[20..22]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(wave[22..24]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wave[24..28]);
        var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wave[28..32]);
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wave[32..34]);
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wave[34..36]);
        var declaredDataLength = BinaryPrimitives.ReadUInt32LittleEndian(wave[40..44]);
        const uint expectedBlockAlign = CapturedAudio.Channels * (CapturedAudio.BitsPerSample / 8);
        const uint expectedByteRate = CapturedAudio.SampleRate * expectedBlockAlign;

        if (riffChunkSize != wave.Length - 8
            || formatChunkSize != 16
            || formatTag != 1
            || channels != CapturedAudio.Channels
            || sampleRate != CapturedAudio.SampleRate
            || byteRate != expectedByteRate
            || blockAlign != expectedBlockAlign
            || bitsPerSample != CapturedAudio.BitsPerSample
            || declaredDataLength == 0
            || declaredDataLength != wave.Length - WaveHeaderLength
            || declaredDataLength % expectedBlockAlign != 0)
        {
            return false;
        }

        var sampleFrames = declaredDataLength / expectedBlockAlign;
        duration = TimeSpan.FromSeconds(sampleFrames / (double)CapturedAudio.SampleRate);
        return true;
    }
}
