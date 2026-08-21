using System.Text.Json.Serialization;
using MirrorPowerAI.Core.Configuration;
using MirrorPowerAI.Core.Gemini;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Transfers per-user application settings; only non-secret properties are serialized to JSON.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Gets the in-memory project context included with answer requests.</summary>
    /// <remarks>This property is excluded from JSON and is persisted separately through DPAPI.</remarks>
    [JsonIgnore]
    public string Context { get; init; } = string.Empty;

    /// <summary>Gets the configured transcription provider identifier.</summary>
    public string TranscriptionProvider { get; init; } = TranscriptionProviders.LocalWhisper;

    /// <summary>Gets the language identifier: <c>es</c>, <c>en</c>, or <c>auto</c>.</summary>
    public string Language { get; init; } = "es";

    /// <summary>Gets the WASAPI render-device identifier.</summary>
    public string AudioDeviceId { get; init; } = AudioDeviceOption.DefaultDeviceId;

    /// <summary>
    /// Gets the internally configured Gemini model identifier.
    /// </summary>
    /// <remarks>
    /// This non-secret value is deliberately not presented in the settings UI. It is constrained to
    /// Gemini model identifiers and takes effect when the application constructs its long-lived client
    /// on the next startup.
    /// </remarks>
    public string GeminiModel { get; init; } = GeminiClientOptions.DefaultModel;

    /// <summary>Gets the accepted Gemini Audio consent revision, or zero when consent was not granted.</summary>
    public int GeminiAudioConsentVersion { get; init; }

    /// <summary>Gets when the current Gemini Audio consent wording was accepted.</summary>
    public DateTimeOffset? GeminiAudioConsentGrantedAtUtc { get; init; }

    /// <summary>Returns a bounded and internally consistent settings value.</summary>
    /// <returns>A normalized copy suitable for persistence.</returns>
    public AppSettings Normalize()
    {
        var provider = TranscriptionProviders.IsSupported(TranscriptionProvider)
            ? TranscriptionProvider
            : TranscriptionProviders.LocalWhisper;
        var language = SupportedLanguages.Contains(Language, StringComparer.OrdinalIgnoreCase)
            ? Language.ToLowerInvariant()
            : "es";
        var audioDeviceId = string.IsNullOrWhiteSpace(AudioDeviceId)
            ? AudioDeviceOption.DefaultDeviceId
            : AudioDeviceId.Trim();
        if (audioDeviceId.Length > 2_048)
        {
            audioDeviceId = AudioDeviceOption.DefaultDeviceId;
        }

        var geminiModel = GeminiClientOptions.NormalizeModelOrDefault(GeminiModel);

        return this with
        {
            Context = (Context ?? string.Empty).Trim().Length <= 16_000
                ? (Context ?? string.Empty).Trim()
                : (Context ?? string.Empty).Trim()[..16_000],
            TranscriptionProvider = provider,
            Language = language,
            AudioDeviceId = audioDeviceId,
            GeminiModel = geminiModel,
            GeminiAudioConsentVersion = provider == TranscriptionProviders.GeminiAudio
                ? Math.Max(0, GeminiAudioConsentVersion)
                : 0,
            GeminiAudioConsentGrantedAtUtc = provider == TranscriptionProviders.GeminiAudio &&
                                             GeminiAudioConsentVersion > 0
                ? GeminiAudioConsentGrantedAtUtc
                : null,
        };
    }

    /// <summary>Maps persisted Windows settings to the shared Core options model.</summary>
    /// <returns>A validated configuration candidate for one new session.</returns>
    public MirrorPowerAIOptions ToCoreOptions()
    {
        var settings = Normalize();
        return new MirrorPowerAIOptions
        {
            Provider = string.Equals(settings.TranscriptionProvider, TranscriptionProviders.GeminiAudio, StringComparison.Ordinal)
                ? MirrorPowerAI.Core.Transcription.TranscriptionProvider.GeminiAudio
                : MirrorPowerAI.Core.Transcription.TranscriptionProvider.LocalWhisper,
            Language = string.Equals(settings.Language, "auto", StringComparison.OrdinalIgnoreCase) ? "es" : settings.Language,
            AutomaticLanguageDetection = string.Equals(settings.Language, "auto", StringComparison.OrdinalIgnoreCase),
            Context = settings.Context,
            OutputDeviceId = settings.AudioDeviceId == AudioDeviceOption.DefaultDeviceId ? null : settings.AudioDeviceId,
            GeminiModel = settings.GeminiModel,
            MaxCaptureDuration = MirrorPowerAIOptions.CaptureDurationLimit,
        };
    }

    private static IReadOnlySet<string> SupportedLanguages { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "es", "en", "auto" };
}

/// <summary>Stable provider identifiers persisted in user settings.</summary>
public static class TranscriptionProviders
{
    /// <summary>Local Whisper transcription; audio remains on the device.</summary>
    public const string LocalWhisper = "LocalWhisper";

    /// <summary>Explicit opt-in Gemini Audio transcription.</summary>
    public const string GeminiAudio = "GeminiAudio";

    /// <summary>Determines whether a provider identifier is supported.</summary>
    /// <param name="provider">The identifier to inspect.</param>
    /// <returns>Whether the identifier is supported.</returns>
    public static bool IsSupported(string? provider) =>
        string.Equals(provider, LocalWhisper, StringComparison.Ordinal) ||
        string.Equals(provider, GeminiAudio, StringComparison.Ordinal);
}

/// <summary>
/// Describes an audio output device shown in the settings window.
/// </summary>
/// <param name="Id">Stable WASAPI endpoint identifier.</param>
/// <param name="DisplayName">User-visible device name.</param>
public sealed record AudioDeviceOption(string Id, string DisplayName)
{
    /// <summary>Identifier used to follow the Windows default output device.</summary>
    public const string DefaultDeviceId = "default";
}

/// <summary>Provides available render devices without coupling settings UI to the audio implementation.</summary>
public interface IAudioDeviceCatalog
{
    /// <summary>Enumerates output devices for the settings window.</summary>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>Available devices, including the Windows default option.</returns>
    Task<IReadOnlyList<AudioDeviceOption>> GetOutputDevicesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Baseline catalog used until the WASAPI implementation supplies endpoint enumeration.
/// </summary>
public sealed class DefaultAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly string _defaultDisplayName;

    /// <summary>Initializes a catalog containing the Windows default output.</summary>
    /// <param name="defaultDisplayName">Localized label for the default output.</param>
    public DefaultAudioDeviceCatalog(string defaultDisplayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDisplayName);
        _defaultDisplayName = defaultDisplayName;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AudioDeviceOption>> GetOutputDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AudioDeviceOption> devices =
            [new AudioDeviceOption(AudioDeviceOption.DefaultDeviceId, _defaultDisplayName)];
        return Task.FromResult(devices);
    }
}
