namespace MirrorPowerAI.Core.Transcription;

/// <summary>
/// Identifies the selected speech transcription implementation.
/// </summary>
public enum TranscriptionProvider
{
    /// <summary>
    /// Transcribes locally with Whisper without uploading the recording.
    /// </summary>
    LocalWhisper,

    /// <summary>
    /// Uploads the recording to Gemini after explicit consent.
    /// </summary>
    GeminiAudio,
}
