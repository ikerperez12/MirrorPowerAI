using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Windows.Transcription;

/// <summary>
/// Resolves a locally verified Whisper model path.
/// </summary>
public interface IWhisperModelPathProvider
{
    /// <summary>
    /// Returns a verified model, downloading it atomically when necessary.
    /// </summary>
    /// <param name="modelDirectory">Application-owned model directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full verified model path.</returns>
    Task<string> EnsureAvailableAsync(
        string modelDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts the supply-chain-verified Core model manager to the Windows transcription service.
/// </summary>
public sealed class WhisperModelPathProvider(WhisperModelManager modelManager)
    : IWhisperModelPathProvider
{
    private readonly WhisperModelManager _modelManager =
        modelManager ?? throw new ArgumentNullException(nameof(modelManager));

    /// <inheritdoc />
    public Task<string> EnsureAvailableAsync(
        string modelDirectory,
        CancellationToken cancellationToken = default) =>
        _modelManager.EnsureAvailableAsync(modelDirectory, cancellationToken);
}

/// <summary>
/// Executes local Whisper inference without exposing Whisper.net types to orchestration code.
/// </summary>
public interface IWhisperInferenceEngine
{
    /// <summary>
    /// Transcribes one normalized in-memory WAVE payload.
    /// </summary>
    /// <param name="modelPath">Verified model path.</param>
    /// <param name="wavData">Normalized WAVE payload.</param>
    /// <param name="language">Whisper language code or <c>auto</c>.</param>
    /// <param name="threadCount">Maximum native inference threads.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Plain transcript text.</returns>
    Task<string> TranscribeAsync(
        string modelPath,
        ReadOnlyMemory<byte> wavData,
        string language,
        int threadCount,
        CancellationToken cancellationToken = default);
}
