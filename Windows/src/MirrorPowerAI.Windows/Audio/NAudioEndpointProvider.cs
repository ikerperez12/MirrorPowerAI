using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MirrorPowerAI.Windows.Audio;

/// <summary>
/// Resolves Windows render endpoints through the NAudio Core Audio adapter.
/// </summary>
public sealed class NAudioEndpointProvider : IAudioEndpointProvider
{
    /// <inheritdoc />
    public IReadOnlyList<AudioEndpoint> GetActiveRenderEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultDeviceId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultDeviceId = defaultDevice.ID;
        }

        var endpoints = new List<AudioEndpoint>();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                endpoints.Add(new AudioEndpoint(
                    device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultDeviceId, StringComparison.Ordinal)));
            }
        }

        return endpoints
            .OrderBy(static endpoint => endpoint.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public AudioEndpoint GetRenderEndpoint(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);

            if (device.State != DeviceState.Active)
            {
                throw new AudioCaptureException(
                    AudioCaptureFailure.DeviceUnavailable,
                    "The selected audio output device is not active.");
            }

            return new AudioEndpoint(
                device.ID,
                device.FriendlyName,
                string.IsNullOrWhiteSpace(deviceId));
        }
        catch (COMException exception)
        {
            throw new AudioCaptureException(
                AudioCaptureFailure.DeviceUnavailable,
                "No usable audio output device is available.",
                exception);
        }
    }

    /// <inheritdoc />
    public bool IsEndpointActive(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            return device.State == DeviceState.Active;
        }
        catch (COMException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsDefaultRenderEndpoint(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return string.Equals(device.ID, deviceId, StringComparison.Ordinal);
        }
        catch (COMException)
        {
            return false;
        }
    }
}
