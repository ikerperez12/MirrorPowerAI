using MirrorPowerAI.Core.Privacy;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Applies an in-process fail-closed privacy barrier to Gemini Audio consent.
/// </summary>
/// <remarks>
/// The barrier starts closed for every application process. A revocation blocks both already-created
/// and future transcription services immediately. It reopens only after the settings UI has durably
/// saved a fresh, explicit cloud-consent choice in that process.
/// </remarks>
public interface IGeminiAudioConsentGate
{
    /// <summary>
    /// Returns the stored consent only while the process-level barrier is open.
    /// </summary>
    /// <param name="persistedConsent">The consent previously read from persisted settings.</param>
    /// <returns>The usable consent, or <see langword="null"/> when uploads are blocked.</returns>
    GeminiAudioConsent? GetEffectiveConsent(GeminiAudioConsent? persistedConsent);

    /// <summary>
    /// Atomically checks current consent and obtains a cancellation token bound to this authorization.
    /// </summary>
    /// <param name="persistedConsent">The consent associated with the prospective upload.</param>
    /// <returns>An authorization that revocation can cancel, or <see langword="null"/> when blocked.</returns>
    GeminiAudioUploadAuthorization? TryAuthorize(GeminiAudioConsent? persistedConsent);

    /// <summary>
    /// Blocks Gemini Audio uploads immediately after a user requests revocation.
    /// </summary>
    void Revoke();

    /// <summary>
    /// Reopens Gemini Audio only after an explicit consent choice was saved successfully.
    /// </summary>
    void AllowAfterSuccessfulExplicitConsentSave();
}

/// <summary>
/// Thread-safe implementation of the process-level Gemini Audio privacy barrier.
/// </summary>
/// <remarks>
/// Persisted consent is retained solely as acceptance history. It never authorizes an upload after an
/// application restart; each new process starts blocked and needs a successful explicit settings save.
/// </remarks>
public sealed class GeminiAudioConsentGate : IGeminiAudioConsentGate, IDisposable
{
    private readonly object _sync = new();
    private readonly List<CancellationTokenSource> _retiredRevocationSources = [];
    private CancellationTokenSource? _revocationSource = CreateCancelledSource();
    private bool _uploadsBlocked = true;
    private bool _disposed;

    /// <inheritdoc />
    public GeminiAudioConsent? GetEffectiveConsent(GeminiAudioConsent? persistedConsent) =>
        TryAuthorize(persistedConsent)?.Consent;

    /// <inheritdoc />
    public GeminiAudioUploadAuthorization? TryAuthorize(GeminiAudioConsent? persistedConsent)
    {
        lock (_sync)
        {
            return _disposed ||
                   _uploadsBlocked ||
                   persistedConsent is null ||
                   !GeminiAudioConsentPolicy.IsValid(persistedConsent) ||
                   _revocationSource is null
                ? null
                : new GeminiAudioUploadAuthorization(persistedConsent, _revocationSource.Token);
        }
    }

    /// <inheritdoc />
    public void Revoke()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _uploadsBlocked = true;
            _revocationSource?.Cancel();
        }
    }

    /// <inheritdoc />
    public void AllowAfterSuccessfulExplicitConsentSave()
    {
        lock (_sync)
        {
            if (_disposed || !_uploadsBlocked)
            {
                return;
            }

            if (_revocationSource is { } previousSource)
            {
                // Keep the cancelled source alive until application shutdown: an authorization
                // returned immediately before revocation may still need to link to its token.
                _retiredRevocationSources.Add(previousSource);
            }

            _revocationSource = new CancellationTokenSource();
            _uploadsBlocked = false;
        }
    }

    /// <summary>
    /// Cancels all active upload authorizations and releases the token sources owned by this gate.
    /// </summary>
    public void Dispose()
    {
        List<CancellationTokenSource> sources;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _uploadsBlocked = true;
            sources = [.. _retiredRevocationSources];
            _retiredRevocationSources.Clear();
            if (_revocationSource is { } activeSource)
            {
                sources.Add(activeSource);
                _revocationSource = null;
            }

            foreach (var source in sources)
            {
                source.Cancel();
            }
        }

        foreach (var source in sources)
        {
            source.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static CancellationTokenSource CreateCancelledSource()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source;
    }
}
