using System.Net;

namespace MirrorPowerAI.Core.Gemini;

/// <summary>
/// Classifies safe-to-display Gemini request failures.
/// </summary>
public enum GeminiErrorKind
{
    /// <summary>The API key is absent or malformed.</summary>
    MissingApiKey,

    /// <summary>The service rejected authentication or authorization.</summary>
    Unauthorized,

    /// <summary>The service rate limit was reached.</summary>
    RateLimited,

    /// <summary>The remote service is temporarily unavailable.</summary>
    ServiceUnavailable,

    /// <summary>The request exceeded its configured time limit.</summary>
    Timeout,

    /// <summary>The service returned an unexpected HTTP status.</summary>
    HttpError,

    /// <summary>The response was too large or was not valid Gemini JSON.</summary>
    InvalidResponse,

    /// <summary>The service blocked the prompt or candidate.</summary>
    Blocked,

    /// <summary>The service returned no usable plain text.</summary>
    EmptyResponse,

    /// <summary>A bounded local input limit was exceeded.</summary>
    InputTooLarge,
}

/// <summary>
/// Represents a typed Gemini failure whose message never contains request or response content.
/// </summary>
public sealed class GeminiApiException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiApiException"/> class.
    /// </summary>
    /// <param name="kind">The stable failure category.</param>
    /// <param name="message">A safe explanation without sensitive content.</param>
    /// <param name="statusCode">The HTTP status code, when one was received.</param>
    public GeminiApiException(
        GeminiErrorKind kind,
        string message,
        HttpStatusCode? statusCode = null)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the stable failure category.
    /// </summary>
    public GeminiErrorKind Kind { get; }

    /// <summary>
    /// Gets the HTTP status code when the service produced one.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }
}
