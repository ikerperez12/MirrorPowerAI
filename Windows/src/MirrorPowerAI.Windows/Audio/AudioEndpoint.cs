namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Describes a Windows render endpoint selected for loopback capture.
/// </summary>
/// <param name="Id">Stable Windows endpoint identifier.</param>
/// <param name="DisplayName">User-facing endpoint name.</param>
/// <param name="WasSelectedAsDefault">Whether the endpoint came from the current system default.</param>
public sealed record AudioEndpoint(string Id, string DisplayName, bool WasSelectedAsDefault);

/// <summary>
/// Resolves and monitors Windows render endpoints without exposing COM or NAudio types.
/// </summary>
public interface IAudioEndpointProvider
{
    /// <summary>
    /// Enumerates active render endpoints for the settings UI.
    /// </summary>
    /// <returns>A stable snapshot ordered by display name.</returns>
    IReadOnlyList<AudioEndpoint> GetActiveRenderEndpoints();

    /// <summary>
    /// Resolves a specific active render endpoint or the current default when no identifier is supplied.
    /// </summary>
    /// <param name="deviceId">Optional Windows endpoint identifier.</param>
    /// <returns>The selected active endpoint.</returns>
    AudioEndpoint GetRenderEndpoint(string? deviceId);

    /// <summary>
    /// Determines whether an endpoint still exists and is active.
    /// </summary>
    /// <param name="deviceId">Windows endpoint identifier.</param>
    /// <returns><see langword="true"/> when the endpoint is active.</returns>
    bool IsEndpointActive(string deviceId);

    /// <summary>
    /// Determines whether an endpoint remains the default multimedia render endpoint.
    /// </summary>
    /// <param name="deviceId">Windows endpoint identifier.</param>
    /// <returns><see langword="true"/> when the endpoint is still the default.</returns>
    bool IsDefaultRenderEndpoint(string deviceId);
}
