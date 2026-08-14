using System.Net.Http;

namespace MirrorPowerAI.Windows.Platform;

/// <summary>
/// Creates the only production HTTP client used for Gemini requests.
/// </summary>
/// <remarks>
/// The Gemini API key is carried in a custom request header. Automatic redirects are therefore
/// forbidden: .NET only guarantees stripping the standard Authorization header on redirects.
/// </remarks>
internal static class GeminiHttpClientFactory
{
    /// <summary>
    /// Creates a handler that never follows a server-provided redirect.
    /// </summary>
    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
    };

    /// <summary>
    /// Creates the long-lived Gemini client with timeout ownership delegated to the request layer.
    /// </summary>
    internal static HttpClient Create() => new(CreateHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
