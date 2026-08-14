using MirrorPowerAI.Core.Answers;
using MirrorPowerAI.Core.Audio;
using MirrorPowerAI.Core.Privacy;
using MirrorPowerAI.Core.Transcription;

namespace MirrorPowerAI.Core.Gemini;

/// <summary>
/// Adapts <see cref="GeminiClient"/> to the textual answer contract.
/// </summary>
public sealed class GeminiAnswerService : IAnswerService
{
    private readonly GeminiClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiAnswerService"/> class.
    /// </summary>
    /// <param name="client">The typed Gemini REST client.</param>
    public GeminiAnswerService(GeminiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public Task<string> AskAsync(
        string question,
        string? context,
        CancellationToken cancellationToken = default) =>
        _client.GenerateAnswerAsync(question, context, cancellationToken);
}

/// <summary>
/// Adapts <see cref="GeminiClient"/> to audio transcription while enforcing current consent.
/// </summary>
public sealed class GeminiAudioTranscriptionService : ITranscriptionService
{
    private readonly GeminiClient _client;
    private readonly Func<GeminiAudioConsent?> _consentProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiAudioTranscriptionService"/> class.
    /// </summary>
    /// <param name="client">The typed Gemini REST client.</param>
    /// <param name="consentProvider">A callback that returns the currently stored consent.</param>
    public GeminiAudioTranscriptionService(
        GeminiClient client,
        Func<GeminiAudioConsent?> consentProvider)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _consentProvider = consentProvider ?? throw new ArgumentNullException(nameof(consentProvider));
    }

    /// <inheritdoc />
    public TranscriptionProvider Provider => TranscriptionProvider.GeminiAudio;

    /// <inheritdoc />
    public Task<string> TranscribeAsync(
        CapturedAudio audio,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (!GeminiAudioConsentPolicy.IsValid(_consentProvider()))
        {
            throw new GeminiAudioConsentRequiredException();
        }

        return _client.TranscribeAudioAsync(audio, language, cancellationToken);
    }
}
