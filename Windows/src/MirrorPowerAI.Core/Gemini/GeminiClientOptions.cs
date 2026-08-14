using System.Text.RegularExpressions;

namespace MirrorPowerAI.Core.Gemini;

/// <summary>
/// Defines bounded, non-secret settings for Gemini REST requests.
/// </summary>
public sealed partial class GeminiClientOptions
{
    private const string AllowedApiHost = "generativelanguage.googleapis.com";
    private const string AllowedApiPath = "/v1beta/";

    /// <summary>
    /// Gets or sets the HTTPS Gemini API base address.
    /// </summary>
    public Uri ApiBaseUri { get; set; } = new("https://generativelanguage.googleapis.com/v1beta/");

    /// <summary>
    /// Gets or sets the model used by both text and audio requests.
    /// </summary>
    public string Model { get; set; } = "gemini-3.5-flash";

    /// <summary>
    /// Gets or sets the maximum duration of one network request.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the maximum question length.
    /// </summary>
    public int MaximumQuestionCharacters { get; set; } = 32_768;

    /// <summary>
    /// Gets or sets the maximum context length.
    /// </summary>
    public int MaximumContextCharacters { get; set; } = 65_536;

    /// <summary>
    /// Gets or sets the maximum raw WAV payload size before Base64 encoding.
    /// </summary>
    public int MaximumAudioBytes { get; set; } = 14 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum accepted response body size.
    /// </summary>
    public int MaximumResponseBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Validates the options without exposing any secret value.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when an option is invalid.</exception>
    public void EnsureValid()
    {
        if (ApiBaseUri is null ||
            !ApiBaseUri.IsAbsoluteUri ||
            ApiBaseUri.Scheme != Uri.UriSchemeHttps ||
            !ApiBaseUri.IsDefaultPort ||
            !string.Equals(ApiBaseUri.IdnHost, AllowedApiHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ApiBaseUri.AbsolutePath, AllowedApiPath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(ApiBaseUri.UserInfo) ||
            !string.IsNullOrEmpty(ApiBaseUri.Query) ||
            !string.IsNullOrEmpty(ApiBaseUri.Fragment))
        {
            throw new ArgumentException(
                "La dirección base debe ser el endpoint oficial HTTPS de Gemini v1beta.",
                nameof(ApiBaseUri));
        }

        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 128 || !ModelPattern().IsMatch(Model))
        {
            throw new ArgumentException("El identificador del modelo Gemini no es válido.", nameof(Model));
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumQuestionCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumQuestionCharacters, 65_536);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumContextCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumContextCharacters, 262_144);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumAudioBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumAudioBytes, 14 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumResponseBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumResponseBytes, 4 * 1024 * 1024);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();
}
