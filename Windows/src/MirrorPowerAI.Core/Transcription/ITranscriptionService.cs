using MirrorPowerAI.Core.Audio;

namespace MirrorPowerAI.Core.Transcription;

/// <summary>
/// Converts normalized audio into plain text using one explicit provider.
/// </summary>
public interface ITranscriptionService
{
    /// <summary>
    /// Gets the provider implemented by this service.
    /// </summary>
    TranscriptionProvider Provider { get; }

    /// <summary>
    /// Transcribes a normalized recording.
    /// </summary>
    /// <param name="audio">The in-memory recording to transcribe.</param>
    /// <param name="language">A BCP-47-like language code or <c>auto</c>.</param>
    /// <param name="cancellationToken">A token used to cancel transcription.</param>
    /// <returns>The plain-text transcript.</returns>
    Task<string> TranscribeAsync(
        CapturedAudio audio,
        string language,
        CancellationToken cancellationToken = default);
}
