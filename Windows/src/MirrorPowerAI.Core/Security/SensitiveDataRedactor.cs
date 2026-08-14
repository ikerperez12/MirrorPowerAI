namespace MirrorPowerAI.Core.Security;

/// <summary>
/// Removes known sensitive values from text intended for diagnostics.
/// </summary>
public static class SensitiveDataRedactor
{
    /// <summary>
    /// Gets the marker used in place of sensitive values.
    /// </summary>
    public const string RedactionMarker = "[REDACTED]";

    /// <summary>
    /// Replaces every supplied non-empty sensitive value using ordinal comparison.
    /// </summary>
    /// <param name="text">The diagnostic text to sanitize.</param>
    /// <param name="sensitiveValues">API keys, context, audio-derived text, or other secrets.</param>
    /// <returns>Sanitized text, or an empty string when <paramref name="text"/> is <see langword="null"/>.</returns>
    public static string Redact(string? text, params string?[] sensitiveValues)
    {
        var redacted = text ?? string.Empty;

        foreach (var value in sensitiveValues
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            redacted = redacted.Replace(value!, RedactionMarker, StringComparison.Ordinal);
        }

        return redacted;
    }
}
