using System.Text.RegularExpressions;
using MirrorPowerAI.Core.Transcription;

namespace MirrorPowerAI.Core.Configuration;

/// <summary>
/// Contains non-secret application settings shared by the Windows adapters.
/// </summary>
public sealed partial class MirrorPowerAIOptions
{
    /// <summary>
    /// Gets the hard upper bound for one capture session.
    /// </summary>
    public static readonly TimeSpan CaptureDurationLimit = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Gets or sets the transcription provider. Local Whisper is the secure default.
    /// </summary>
    public TranscriptionProvider Provider { get; set; } = TranscriptionProvider.LocalWhisper;

    /// <summary>
    /// Gets or sets the transcription language.
    /// </summary>
    public string Language { get; set; } = "es";

    /// <summary>
    /// Gets or sets a value indicating whether the selected provider should detect the language.
    /// </summary>
    public bool AutomaticLanguageDetection { get; set; }

    /// <summary>
    /// Gets or sets optional project context sent only with the textual answer request.
    /// </summary>
    public string Context { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the output device identifier, or <see langword="null"/> for the system default.
    /// </summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>
    /// Gets or sets the Gemini model identifier.
    /// </summary>
    public string GeminiModel { get; set; } = "gemini-3.5-flash";

    /// <summary>
    /// Gets or sets the maximum duration of one capture session.
    /// </summary>
    public TimeSpan MaxCaptureDuration { get; set; } = CaptureDurationLimit;

    /// <summary>
    /// Validates all non-secret configuration values.
    /// </summary>
    /// <returns>A read-only collection of validation errors.</returns>
    public IReadOnlyList<ConfigurationValidationError> Validate()
    {
        var errors = new List<ConfigurationValidationError>();

        if (!Enum.IsDefined(Provider))
        {
            errors.Add(new(nameof(Provider), "El proveedor de transcripción no es válido."));
        }

        if (!AutomaticLanguageDetection &&
            (string.IsNullOrWhiteSpace(Language) || !LanguageCodePattern().IsMatch(Language)))
        {
            errors.Add(new(nameof(Language), "El idioma debe ser un código válido, por ejemplo es o es-ES."));
        }

        if (Context is null || Context.Length > 65_536)
        {
            errors.Add(new(nameof(Context), "El contexto debe existir y no superar 65 536 caracteres."));
        }

        if (OutputDeviceId?.Length > 2_048)
        {
            errors.Add(new(nameof(OutputDeviceId), "El identificador del dispositivo es demasiado largo."));
        }

        if (string.IsNullOrWhiteSpace(GeminiModel) ||
            GeminiModel.Length > 128 ||
            !ModelNamePattern().IsMatch(GeminiModel))
        {
            errors.Add(new(nameof(GeminiModel), "El identificador del modelo Gemini no es válido."));
        }

        if (MaxCaptureDuration <= TimeSpan.Zero || MaxCaptureDuration > CaptureDurationLimit)
        {
            errors.Add(new(nameof(MaxCaptureDuration), "La captura debe durar entre 1 milisegundo y 300 segundos."));
        }

        return errors;
    }

    /// <summary>
    /// Throws when this instance contains invalid values.
    /// </summary>
    /// <exception cref="ConfigurationValidationException">Thrown when validation fails.</exception>
    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }
    }

    [GeneratedRegex("^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCodePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelNamePattern();
}

/// <summary>
/// Describes one invalid configuration property without including sensitive values.
/// </summary>
/// <param name="PropertyName">The invalid property name.</param>
/// <param name="Message">A safe, localized explanation.</param>
public sealed record ConfigurationValidationError(string PropertyName, string Message);

/// <summary>
/// Indicates that application configuration failed validation.
/// </summary>
public sealed class ConfigurationValidationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationValidationException"/> class.
    /// </summary>
    /// <param name="errors">The validation errors.</param>
    public ConfigurationValidationException(IReadOnlyList<ConfigurationValidationError> errors)
        : base("La configuración de MirrorPowerAI no es válida.")
    {
        Errors = errors ?? throw new ArgumentNullException(nameof(errors));
    }

    /// <summary>
    /// Gets the validation errors.
    /// </summary>
    public IReadOnlyList<ConfigurationValidationError> Errors { get; }
}
