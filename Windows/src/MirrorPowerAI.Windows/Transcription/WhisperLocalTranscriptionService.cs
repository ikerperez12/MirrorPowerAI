using System.Buffers.Binary;
using System.IO;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Models;
using MirrorPowerAI.Core.Transcription;

namespace MirrorPowerAI.Windows.Transcription;

/// <summary>
/// Transcribes normalized audio locally with an explicitly verified Whisper model.
/// </summary>
public sealed class WhisperLocalTranscriptionService : ITranscriptionService
{
    private const int WaveHeaderLength = 44;
    private static readonly TimeSpan MaximumAudioDuration = TimeSpan.FromMinutes(5);
    private readonly IWhisperModelLeaseProvider _modelLeaseProvider;
    private readonly IWhisperInferenceEngine _inferenceEngine;
    private readonly string _modelDirectory;
    private readonly int _threadCount;

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
        _modelDirectory = Path.GetFullPath(modelDirectory);
        _threadCount = effectiveThreadCount;
    }

    /// <inheritdoc />
    public TranscriptionProvider Provider => TranscriptionProvider.LocalWhisper;

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

        if (audio.Duration > MaximumAudioDuration || !IsNormalizedWave(audio.WavData.Span))
        {
            throw new WhisperTranscriptionException(
                WhisperTranscriptionFailure.InvalidAudio,
                "El audio no tiene el formato PCM normalizado requerido.");
        }

        var normalizedLanguage = NormalizeLanguage(language);
        using var modelLease = await _modelLeaseProvider
            .AcquireVerifiedLeaseAsync(_modelDirectory, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var transcript = await _inferenceEngine
                .TranscribeAsync(
                    modelLease.ModelPath,
                    audio.WavData,
                    normalizedLanguage,
                    _threadCount,
                    cancellationToken)
                .ConfigureAwait(false);

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

    private static bool IsNormalizedWave(ReadOnlySpan<byte> wave)
    {
        if (wave.Length < WaveHeaderLength
            || !wave[..4].SequenceEqual("RIFF"u8)
            || !wave[8..12].SequenceEqual("WAVE"u8)
            || !wave[12..16].SequenceEqual("fmt "u8)
            || !wave[36..40].SequenceEqual("data"u8))
        {
            return false;
        }

        var formatChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wave[16..20]);
        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(wave[20..22]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(wave[22..24]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wave[24..28]);
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wave[34..36]);
        var declaredDataLength = BinaryPrimitives.ReadUInt32LittleEndian(wave[40..44]);

        return formatChunkSize == 16
            && formatTag == 1
            && channels == CapturedAudio.Channels
            && sampleRate == CapturedAudio.SampleRate
            && bitsPerSample == CapturedAudio.BitsPerSample
            && declaredDataLength == wave.Length - WaveHeaderLength
            && declaredDataLength % sizeof(short) == 0;
    }
}
