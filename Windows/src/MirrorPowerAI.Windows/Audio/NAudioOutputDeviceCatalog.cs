using System.Runtime.InteropServices;
using MirrorPowerAI.Windows.Platform;

namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Exposes NAudio render endpoints through the settings-window device catalog.
/// </summary>
public sealed class NAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly IAudioEndpointProvider _endpointProvider;
    private readonly string _defaultDisplayName;

    /// <summary>
    /// Initializes a device catalog.
    /// </summary>
    /// <param name="endpointProvider">Render endpoint adapter.</param>
    /// <param name="defaultDisplayName">Localized label for following the Windows default device.</param>
    public NAudioDeviceCatalog(
        IAudioEndpointProvider endpointProvider,
        string defaultDisplayName)
    {
        ArgumentNullException.ThrowIfNull(endpointProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDisplayName);
        _endpointProvider = endpointProvider;
        _defaultDisplayName = defaultDisplayName;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AudioDeviceOption>> GetOutputDevicesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = new List<AudioDeviceOption>
        {
            new(AudioDeviceOption.DefaultDeviceId, _defaultDisplayName),
        };
        try
        {
            devices.AddRange(_endpointProvider
                .GetActiveRenderEndpoints()
                .Select(static endpoint => new AudioDeviceOption(endpoint.Id, endpoint.DisplayName)));
        }
        catch (COMException)
        {
            // Settings remains usable and can continue following the default endpoint.
        }

        return Task.FromResult<IReadOnlyList<AudioDeviceOption>>(devices);
    }
}
