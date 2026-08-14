using MirrorPowerAI.Windows.Audio;
using MirrorPowerAI.Windows.Platform;

namespace MirrorPowerAI.Windows.Tests.Audio;

public sealed class NAudioDeviceCatalogTests
{
    [Fact]
    public async Task GetOutputDevicesAsync_AlwaysPrependsFollowDefaultOption()
    {
        // Arrange
        var provider = new FakeEndpointProvider(
            [
                new AudioEndpoint("speakers", "Speakers", true),
                new AudioEndpoint("headset", "USB Headset", false),
            ]);
        var catalog = new MirrorPowerAI.Windows.Audio.NAudioDeviceCatalog(
            provider,
            "Predeterminado de Windows");

        // Act
        var devices = await catalog.GetOutputDevicesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(3, devices.Count);
        Assert.Equal(AudioDeviceOption.DefaultDeviceId, devices[0].Id);
        Assert.Equal("speakers", devices[1].Id);
        Assert.Equal("headset", devices[2].Id);
    }

    [Fact]
    public async Task GetOutputDevicesAsync_Cancelled_DoesNotEnumerateNativeEndpoints()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new FakeEndpointProvider([]);
        var catalog = new MirrorPowerAI.Windows.Audio.NAudioDeviceCatalog(provider, "Default");

        // Act and assert
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalog.GetOutputDevicesAsync(cancellation.Token));
        Assert.Equal(0, provider.EnumerationCount);
    }

    [Fact]
    public async Task GetOutputDevicesAsync_CoreAudioUnavailable_KeepsDefaultOption()
    {
        // Arrange
        var provider = new FakeEndpointProvider([]) { ThrowComException = true };
        var catalog = new MirrorPowerAI.Windows.Audio.NAudioDeviceCatalog(provider, "Default");

        // Act
        var devices = await catalog.GetOutputDevicesAsync(CancellationToken.None);

        // Assert
        var device = Assert.Single(devices);
        Assert.Equal(AudioDeviceOption.DefaultDeviceId, device.Id);
    }

    private sealed class FakeEndpointProvider(IReadOnlyList<AudioEndpoint> endpoints)
        : IAudioEndpointProvider
    {
        public int EnumerationCount { get; private set; }

        public bool ThrowComException { get; init; }

        public IReadOnlyList<AudioEndpoint> GetActiveRenderEndpoints()
        {
            EnumerationCount++;
            if (ThrowComException)
            {
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(
                    unchecked((int)0x80004005));
            }

            return endpoints;
        }

        public AudioEndpoint GetRenderEndpoint(string? deviceId) => throw new NotSupportedException();

        public bool IsEndpointActive(string deviceId) => throw new NotSupportedException();

        public bool IsDefaultRenderEndpoint(string deviceId) => throw new NotSupportedException();
    }
}
