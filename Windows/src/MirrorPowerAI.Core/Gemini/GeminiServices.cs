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
    private readonly Func<GeminiAudioUploadAuthorization?>? _uploadAuthorizationProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiAudioTranscriptionService"/> class.
    /// </summary>
    /// <param name="client">The typed Gemini REST client.</param>
    /// <param name="consentProvider">A callback that returns the currently stored consent.</param>
    /// <param name="uploadAuthorizationProvider">
    /// Optional process-level authorization callback. When present it supplies a revocation token
    /// that remains linked to the request until the HTTP operation finishes.
    /// </param>
    public GeminiAudioTranscriptionService(
        GeminiClient client,
        Func<GeminiAudioConsent?> consentProvider,
        Func<GeminiAudioUploadAuthorization?>? uploadAuthorizationProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _consentProvider = consentProvider ?? throw new ArgumentNullException(nameof(consentProvider));
        _uploadAuthorizationProvider = uploadAuthorizationProvider;
    }

    /// <inheritdoc />
    public TranscriptionProvider Provider => TranscriptionProvider.GeminiAudio;

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(
        CapturedAudio audio,
        string language,
        CancellationToken cancellationToken = default)
    {
        GeminiAudioUploadAuthorization? authorization;
        if (_uploadAuthorizationProvider is null)
        {
            var consent = _consentProvider();
            if (consent is null || !GeminiAudioConsentPolicy.IsValid(consent))
            {
                throw new GeminiAudioConsentRequiredException();
            }

            authorization = new GeminiAudioUploadAuthorization(consent, CancellationToken.None);
        }
        else
        {
            authorization = _uploadAuthorizationProvider();
            if (authorization is null)
            {
                throw new GeminiAudioConsentRequiredException();
            }
        }

        if (!GeminiAudioConsentPolicy.IsValid(authorization.Consent))
        {
            throw new GeminiAudioConsentRequiredException();
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            authorization.RevocationToken);
        try
        {
            return await _client
                .TranscribeAudioAsync(audio, language, requestCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            authorization.RevocationToken.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new GeminiAudioConsentRequiredException();
        }
    }
}
