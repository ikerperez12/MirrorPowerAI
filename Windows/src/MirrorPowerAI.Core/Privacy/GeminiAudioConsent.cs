namespace MirrorPowerAI.Core.Privacy;

/// <summary>
/// Records explicit consent to upload audio to Gemini under a specific policy version.
/// </summary>
/// <param name="Version">The policy version accepted by the user.</param>
/// <param name="GrantedAtUtc">The UTC instant at which consent was granted.</param>
public sealed record GeminiAudioConsent(int Version, DateTimeOffset GrantedAtUtc);

/// <summary>
/// Captures one in-process authorization to upload audio to Gemini.
/// </summary>
/// <remarks>
/// The revocation token is owned by the platform shell. It is cancelled when the user withdraws
/// consent so an upload that is still preparing can be stopped before it reaches the network.
/// </remarks>
/// <param name="Consent">The current, versioned consent associated with this authorization.</param>
/// <param name="RevocationToken">A token cancelled when this authorization is withdrawn.</param>
public sealed record GeminiAudioUploadAuthorization(
    GeminiAudioConsent Consent,
    CancellationToken RevocationToken);

/// <summary>
/// Creates and validates versioned consent for Gemini audio transcription.
/// </summary>
public static class GeminiAudioConsentPolicy
{
    /// <summary>
    /// Gets the consent wording version required by this build.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Creates a consent record for the current policy.
    /// </summary>
    /// <param name="grantedAtUtc">The grant time, or the current UTC time when omitted.</param>
    /// <returns>A current consent record.</returns>
    public static GeminiAudioConsent Grant(DateTimeOffset? grantedAtUtc = null) =>
        new(CurrentVersion, grantedAtUtc ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// Determines whether a consent record is present, current, and temporally valid.
    /// </summary>
    /// <param name="consent">The stored consent record.</param>
    /// <returns><see langword="true"/> when Gemini audio may be used.</returns>
    public static bool IsValid(GeminiAudioConsent? consent) =>
        consent is { Version: CurrentVersion } &&
        consent.GrantedAtUtc > DateTimeOffset.UnixEpoch &&
        consent.GrantedAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5);
}

/// <summary>
/// Indicates that Gemini audio transcription was attempted without current explicit consent.
/// </summary>
public sealed class GeminiAudioConsentRequiredException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiAudioConsentRequiredException"/> class.
    /// </summary>
    public GeminiAudioConsentRequiredException()
        : base("Se necesita consentimiento explícito y actualizado para enviar audio a Gemini.")
    {
    }
}
